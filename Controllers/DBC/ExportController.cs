using DBCD;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text;
using wow.tools.local;
using wow.tools.local.Managers;
using wow.tools.local.Providers;
using wow.tools.local.Services;
using wow.tools.local.Utils;

namespace wow.tools.Local.Controllers
{
    using static TACTSharp.RootInstance;
    using Parameters = IReadOnlyDictionary<string, string>;

    [Route("dbc/export")]
    [ApiController]
    public class ExportController(IDBCManager dbcManager) : ControllerBase
    {
        private const int MaxBulkExportTables = 2048;
        private const long MaxBulkExportUncompressedBytes = 2L * 1024 * 1024 * 1024;
        private const long MaxBulkExportCompressedBytes = 1024L * 1024 * 1024;

        private static readonly Parameters DefaultParameters = new Dictionary<string, string>();
        private static readonly char[] QuoteableChars = [',', '"', '\r', '\n'];
        private static readonly SemaphoreSlim BulkExportGate = new(1, 1);

        private readonly DBCManager dbcManager = (DBCManager)dbcManager;

        private static async Task<byte[]> GenerateCSVStream(IDBCDStorage storage, Parameters parameters, bool newLinesInStrings = true)
        {
            using var exportStream = new MemoryStream();
            await WriteCSVStream(exportStream, storage, parameters, newLinesInStrings, CancellationToken.None);
            return exportStream.ToArray();
        }

        private static async Task WriteCSVStream(Stream exportStream, IDBCDStorage storage, Parameters parameters, bool newLinesInStrings, CancellationToken cancellationToken)
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            if (storage.AvailableColumns.Length == 0)
            {
                throw new Exception("No columns found!");
            }

            // NOTE: if newLinesInStrings is obsolete then use StringToCSVCell in ctor
            Func<string, string> formatter = newLinesInStrings switch
            {
                true => StringToCSVCell,
                _ => StringToCSVCellSingleLine
            };

            var viewFilter = new DBCViewFilter(storage, parameters, formatter);

            using var exportWriter = new StreamWriter(exportStream, new UTF8Encoding(false), 1024, leaveOpen: true);

            // write header
            await exportWriter.WriteLineAsync(string.Join(",", GetColumnNames(storage)).AsMemory(), cancellationToken);

            // write records
            foreach (var item in viewFilter.GetRecords(cancellationToken))
                await exportWriter.WriteLineAsync(string.Join(",", item).AsMemory(), cancellationToken);

            await exportWriter.FlushAsync(cancellationToken);
        }

        [Route("")]
        [Route("csv")]
        [HttpGet, HttpPost]
        public async Task<ActionResult> ExportCSV(string name, string build, bool useHotfixes = false, bool newLinesInStrings = true, LocaleFlags locale = LocaleFlags.All_WoW)
        {
            Console.WriteLine("Exporting DBC " + name + " as CSV for build " + build + " and locale " + locale);

            var parameters = DefaultParameters;

            if (Request.Method == "POST")
                parameters = (IReadOnlyDictionary<string, string>)Request.Form.ToDictionary(x => x.Key, x => (string)x.Value!);

            try
            {
                var storage = await GetStorage(name, build, useHotfixes, locale);

                if (storage.Count == 0)
                {
                    return NoContent();
                }

                return new FileContentResult(await GenerateCSVStream(storage, parameters, newLinesInStrings), "application/octet-stream")
                {
                    FileDownloadName = Path.ChangeExtension(name, ".csv")
                };
            }
            catch (FileNotFoundException e)
            {
                Console.WriteLine("DBC " + name + " for build " + build + " not found: " + e.Message);
                return NotFound();
            }
            catch (Exception e)
            {
                Console.WriteLine("Error during CSV generation for DBC " + name + " for build " + build + ": " + e.Message);
                return BadRequest();
            }
        }

        [Route("alltodisk")]
        [HttpGet]
        public bool ExportAllToDisk(LocaleFlags locale = LocaleFlags.All_WoW)
        {
            Console.WriteLine("Exporting all DBCs from current build to disk");

            foreach (var dbname in dbcManager.GetDBCNames(CASC.BuildName))
            {
                try
                {
                    var cleanName = dbname.ToLower();
                    var filestream = CASC.GetDB2ByName("dbfilesclient/" + dbname + ".db2", locale);

                    if (!Directory.Exists(Path.Combine(SettingsManager.DBCFolder, CASC.BuildName, "dbfilesclient")))
                    {
                        Directory.CreateDirectory(Path.Combine(SettingsManager.DBCFolder, CASC.BuildName, "dbfilesclient"));
                    }

                    using (var exportStream = new FileStream(Path.Combine(SettingsManager.DBCFolder, CASC.BuildName, "dbfilesclient", cleanName + ".db2"), FileMode.Create))
                    {
                        filestream.CopyTo(exportStream);
                    }
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine("Table " + dbname + " not found in build");
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error " + e.Message + " occured when extracting DB2 " + dbname);
                }
            }

            return true;
        }

        [Route("all")]
        [HttpGet]
        public async Task ExportAllCSV(string? build, bool useHotfixes = false, bool newLinesInStrings = true, LocaleFlags locale = LocaleFlags.All_WoW, CancellationToken cancellationToken = default)
        {
            Console.WriteLine("Exporting all DBCs build " + build + " and locale " + locale);
            if (build == null)
                build = CASC.BuildName;

            var fetchSite = Request.Headers["Sec-Fetch-Site"].ToString();
            if (!string.IsNullOrEmpty(fetchSite) &&
                !string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase))
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                await Response.WriteAsync("Cross-origin bulk exports are not allowed.", cancellationToken);
                return;
            }

            if (!await BulkExportGate.WaitAsync(0, cancellationToken))
            {
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                Response.Headers["Retry-After"] = "60";
                await Response.WriteAsync("A bulk export is already in progress.", cancellationToken);
                return;
            }

            try
            {
                var tableNames = dbcManager.GetDBCNames(build);
                if (tableNames.Length > MaxBulkExportTables)
                {
                    Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await Response.WriteAsync($"Bulk exports are limited to {MaxBulkExportTables} tables.", cancellationToken);
                    return;
                }

                Response.ContentType = "application/octet-stream";
                Response.Headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{Uri.EscapeDataString("alldbc-" + build + "-" + locale + ".zip")}";

                var compressedBudget = new ByteBudget(MaxBulkExportCompressedBytes, "compressed bulk export");
                var uncompressedBudget = new ByteBudget(MaxBulkExportUncompressedBytes, "uncompressed bulk export");

                var bodyControlFeature = HttpContext.Features.Get<IHttpBodyControlFeature>();
                if (bodyControlFeature != null)
                    bodyControlFeature.AllowSynchronousIO = true;

                using var limitedResponseStream = new BudgetedWriteStream(Response.Body, compressedBudget, leaveOpen: true);
                using (var archive = new ZipArchive(limitedResponseStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var dbname in tableNames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        IDBCDStorage storage;
                        try
                        {
                            var cleanName = dbname.ToLower();
                            storage = await GetStorage(cleanName, build, useHotfixes, locale);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (FileNotFoundException)
                        {
                            Console.WriteLine("Table " + dbname + " not found in build " + build);
                            continue;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error " + e.Message + " occured when getting table " + dbname + " of build " + build);
                            continue;
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        var entryname = dbname.ToLower() + ".csv";
                        var entry = archive.CreateEntry(entryname, CompressionLevel.Fastest);
                        using (var entryStream = entry.Open())
                        using (var limitedEntryStream = new BudgetedWriteStream(entryStream, uncompressedBudget, leaveOpen: true))
                        {
                            await WriteCSVStream(limitedEntryStream, storage, DefaultParameters, newLinesInStrings, cancellationToken);
                        }
                    }
                }

                await limitedResponseStream.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                HttpContext.Abort();
            }
            catch (ExportLimitExceededException e)
            {
                Console.WriteLine("Bulk DBC export aborted: " + e.Message);
                HttpContext.Abort();
            }
            catch (Exception e) when (Response.HasStarted)
            {
                Console.WriteLine("Bulk DBC export aborted after the response started: " + e.Message);
                HttpContext.Abort();
            }
            finally
            {
                BulkExportGate.Release();
            }
        }

        [Route("db2")]
        [HttpGet]
        public async Task<ActionResult> GetDB2ByTableName(string tableName, string fullBuild, LocaleFlags locale = LocaleFlags.All_WoW)
        {
            var provider = new DBCProvider();

            Console.WriteLine("Serving DB2 \"" + tableName + "\" for build " + fullBuild);

            try
            {

                if (fullBuild == CASC.BuildName)
                {
                    // Load from CASC
                    var fullFileName = "dbfilesclient/" + tableName + ".db2";
                    return new FileStreamResult(CASC.GetDB2ByName(fullFileName, locale), "application/octet-stream")
                    {
                        FileDownloadName = Path.GetFileName(tableName.ToLower() + ".db2")
                    };
                }
                else
                {
                    string directoryPath = Path.Combine(SettingsManager.DBCFolder, fullBuild, "dbfilesclient");
                    if (!Directory.Exists(directoryPath))
                        throw new FileNotFoundException($"Unable to DBC/DB2 folder for {fullBuild}, are DBC/DB2 for this build extracted?");

                    // Try to find a matching dbc or db2 file, ignoring case sensitivity to maintain the same behavior on all platforms
                    string? fileName = Directory.EnumerateFiles(directoryPath).FirstOrDefault(fn =>
                        Path.GetFileName(fn).Equals($"{tableName}.db2", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(fn).Equals($"{tableName}.dbc", StringComparison.OrdinalIgnoreCase));

                    if (fileName == null)
                        throw new FileNotFoundException($"Unable to find {tableName}, are DB2s/DBCs for this build extracted?");

                    string? extension = Path.GetExtension(fileName); // includes the period char as well
                    if (extension == null)
                        throw new FileNotFoundException($"Unable to find a valid DBC/DB2 {tableName} table with valid file extension.");

                    using (var stream = provider.StreamForTableName(tableName, fullBuild))
                    using (var ms = new MemoryStream())
                    {
                        await stream.CopyToAsync(ms);
                        return new FileContentResult(ms.ToArray(), "application/octet-stream")
                        {
                            FileDownloadName = Path.GetFileName(tableName.ToLower() + extension)
                        };
                    }
                }

            }
            catch (FileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Table " + tableName + " not found for build " + fullBuild);
                Console.ResetColor();
                return NotFound();
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error " + e.Message + " occured when serving file " + tableName + " for build " + fullBuild);
                Console.ResetColor();
            }

            return NotFound();
        }
        private async Task<IDBCDStorage> GetStorage(string name, string build, bool useHotfixes = false, LocaleFlags locale = LocaleFlags.All_WoW)
        {
            return await dbcManager.GetOrLoad(name, build, useHotfixes, locale);
        }

        private static IEnumerable<string> GetColumnNames(IDBCDStorage storage)
        {
            var record = storage.Values.FirstOrDefault();

            if (record == null)
                yield break;

            for (var i = 0; i < storage.AvailableColumns.Length; ++i)
            {
                var name = storage.AvailableColumns[i];

                if (record[name] is Array array)
                {
                    // explode arrays by suffixing the ordinal
                    for (var j = 0; j < array.Length; j++)
                        yield return name + $"[{j}]";
                }
                else
                {
                    yield return name;
                }
            }
        }

        private static string StringToCSVCell(string str)
        {
            var mustQuote = str.IndexOfAny(QuoteableChars) > -1;
            if (mustQuote)
                return '"' + str.Replace("\"", "\"\"") + '"';

            return str;
        }

        private static string StringToCSVCellSingleLine(string str)
        {
            return StringToCSVCell(str.Replace("\n", "").Replace("\r", ""));
        }

        private sealed class ByteBudget(long limit, string description)
        {
            private long bytesWritten;

            public void Reserve(int byteCount)
            {
                if (byteCount > limit - bytesWritten)
                    throw new ExportLimitExceededException($"The {description} exceeded its {limit}-byte limit.");

                bytesWritten += byteCount;
            }
        }

        private sealed class BudgetedWriteStream(Stream inner, ByteBudget budget, bool leaveOpen) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                budget.Reserve(count);
                inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                budget.Reserve(buffer.Length);
                inner.Write(buffer);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                budget.Reserve(count);
                return inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                budget.Reserve(buffer.Length);
                return inner.WriteAsync(buffer, cancellationToken);
            }

            public override void WriteByte(byte value)
            {
                budget.Reserve(1);
                inner.WriteByte(value);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !leaveOpen)
                    inner.Dispose();

                base.Dispose(disposing);
            }
        }

        private sealed class ExportLimitExceededException(string message) : IOException(message);
    }
}
