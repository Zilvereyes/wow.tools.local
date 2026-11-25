using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text;
using wow.tools.local.Services;

namespace wow.tools.local.Controllers
{
    [Route("casc/[controller]")]
    [ApiController]
    public class ZipController : ControllerBase
    {
        private const int MaxFilesPerArchive = 1000;
        private const long MaxArchiveBytes = 100_000_000;
        private const int MaxConcurrentArchiveJobs = 2;
        private static readonly SemaphoreSlim ArchiveJobLimiter = new(MaxConcurrentArchiveJobs, MaxConcurrentArchiveJobs);

        [Route("fdids")]
        [HttpGet]
        public async Task<ActionResult> GetByFileDataID(string? ids, string filename)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return BadRequest("At least one file data ID is required");

            var requestedIDCount = 1;
            foreach (var character in ids)
            {
                if (character == ',' && ++requestedIDCount > MaxFilesPerArchive)
                    return BadRequest($"A maximum of {MaxFilesPerArchive} file data IDs is allowed per archive");
            }

            var requestedIDs = ids.Split(',');

            var filedataids = new List<uint>(requestedIDs.Length);
            var uniqueFileDataIDs = new HashSet<uint>();
            foreach (var requestedID in requestedIDs)
            {
                if (!uint.TryParse(requestedID, out var filedataid))
                    return BadRequest($"Invalid file data ID: {requestedID}");

                if (!uniqueFileDataIDs.Add(filedataid))
                    return BadRequest($"Duplicate file data ID: {filedataid}");

                filedataids.Add(filedataid);
            }

            Console.WriteLine("Serving zip file \"" + filename + "\" (" + filedataids.Count + " fdids starting with " + filedataids[0].ToString() + ")");

            var errors = new List<string>();
            if (!await ArchiveJobLimiter.WaitAsync(0, HttpContext.RequestAborted))
                return StatusCode(StatusCodes.Status429TooManyRequests, "Too many archive jobs are already in progress");

            SizeLimitedStream zip;
            try
            {
                zip = CreateArchiveStream();
            }
            catch
            {
                ArchiveJobLimiter.Release();
                throw;
            }

            try
            {
                var remainingUncompressedBytes = MaxArchiveBytes;
                var copyBuffer = new byte[81920];

                using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
                {
                    foreach (var filedataid in filedataids)
                    {
                        try
                        {
                            using (var cascStream = CASC.GetFileByID(filedataid, remainingUncompressedBytes))
                            {
                                if (cascStream == null)
                                {
                                    errors.Add("Error opening file " + filedataid);
                                    Console.WriteLine("File " + filedataid + " not found in CASC");
                                    continue;
                                }

                                var entryname = Path.GetFileName(Listfile.NameMap[(int)filedataid]);
                                if (entryname == "")
                                {
                                    entryname = filedataid.ToString() + ".unk";
                                }

                                var entry = archive.CreateEntry(entryname);
                                using (var entryStream = entry.Open())
                                {
                                    CopyWithLimit(cascStream, entryStream, copyBuffer, ref remainingUncompressedBytes);
                                }
                            }
                        }
                        catch (ArchiveSizeLimitExceededException)
                        {
                            throw;
                        }
                        catch (FileNotFoundException)
                        {
                            errors.Add("File " + filedataid + " not found in root");
                            Console.WriteLine("File " + filedataid + " not found in root of buildconfig");
                        }
                        catch (Exception e)
                        {
                            errors.Add("Error " + e.Message + " occured when getting file " + filedataid);
                            Console.WriteLine("Error " + e.Message + " occured when getting file " + filedataid);
                        }
                    }

                    if (errors.Count > 0)
                    {
                        var entry = archive.CreateEntry("errors.txt");
                        using (var entryStream = entry.Open())
                        {
                            foreach (var error in errors)
                            {
                                var errorBytes = Encoding.UTF8.GetBytes(error + "\n");
                                WriteWithLimit(entryStream, errorBytes, ref remainingUncompressedBytes);
                            }
                        }
                    }
                }

                zip.Position = 0;
                return new FileStreamResult(zip, "application/octet-stream")
                {
                    FileDownloadName = filename
                };
            }
            catch (ArchiveSizeLimitExceededException)
            {
                zip.Dispose();
                return StatusCode(StatusCodes.Status413PayloadTooLarge, "Archive exceeds the 100 MB size limit");
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        private static SizeLimitedStream CreateArchiveStream()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var fileStream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan
            });

            return new SizeLimitedStream(fileStream, MaxArchiveBytes, () => ArchiveJobLimiter.Release());
        }

        private static void CopyWithLimit(Stream source, Stream destination, byte[] buffer, ref long remainingBytes)
        {
            while (true)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, remainingBytes + 1);
                var bytesRead = source.Read(buffer, 0, bytesToRead);
                if (bytesRead == 0)
                    return;

                if (bytesRead > remainingBytes)
                    throw new ArchiveSizeLimitExceededException();

                destination.Write(buffer, 0, bytesRead);
                remainingBytes -= bytesRead;
            }
        }

        private static void WriteWithLimit(Stream destination, byte[] buffer, ref long remainingBytes)
        {
            if (buffer.Length > remainingBytes)
                throw new ArchiveSizeLimitExceededException();

            destination.Write(buffer);
            remainingBytes -= buffer.Length;
        }

        private sealed class ArchiveSizeLimitExceededException : IOException
        {
        }

        private sealed class SizeLimitedStream : Stream
        {
            private readonly Stream innerStream;
            private readonly long maxLength;
            private readonly Action onDispose;
            private bool disposed;

            public SizeLimitedStream(Stream innerStream, long maxLength, Action onDispose)
            {
                this.innerStream = innerStream;
                this.maxLength = maxLength;
                this.onDispose = onDispose;
            }

            public override bool CanRead => innerStream.CanRead;
            public override bool CanSeek => innerStream.CanSeek;
            public override bool CanWrite => innerStream.CanWrite;
            public override long Length => innerStream.Length;
            public override long Position
            {
                get => innerStream.Position;
                set
                {
                    if (value > maxLength)
                        throw new ArchiveSizeLimitExceededException();

                    innerStream.Position = value;
                }
            }

            public override void Flush() => innerStream.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => innerStream.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => innerStream.Read(buffer, offset, count);

            public override int Read(Span<byte> buffer) => innerStream.Read(buffer);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                innerStream.ReadAsync(buffer, offset, count, cancellationToken);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                innerStream.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin)
            {
                var originalPosition = innerStream.Position;
                var newPosition = innerStream.Seek(offset, origin);
                if (newPosition <= maxLength)
                    return newPosition;

                innerStream.Position = originalPosition;
                throw new ArchiveSizeLimitExceededException();
            }

            public override void SetLength(long value)
            {
                if (value > maxLength)
                    throw new ArchiveSizeLimitExceededException();

                innerStream.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                EnsureWriteFits(count);
                innerStream.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                EnsureWriteFits(buffer.Length);
                innerStream.Write(buffer);
            }

            public override void WriteByte(byte value)
            {
                EnsureWriteFits(1);
                innerStream.WriteByte(value);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                EnsureWriteFits(count);
                return innerStream.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                EnsureWriteFits(buffer.Length);
                return innerStream.WriteAsync(buffer, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (!disposing || disposed)
                {
                    base.Dispose(disposing);
                    return;
                }

                disposed = true;
                try
                {
                    innerStream.Dispose();
                }
                finally
                {
                    onDispose();
                    base.Dispose(disposing);
                }
            }

            public override async ValueTask DisposeAsync()
            {
                if (disposed)
                    return;

                disposed = true;
                try
                {
                    await innerStream.DisposeAsync();
                }
                finally
                {
                    onDispose();
                    GC.SuppressFinalize(this);
                }
            }

            private void EnsureWriteFits(int count)
            {
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));

                if (innerStream.Position > maxLength || count > maxLength - innerStream.Position)
                    throw new ArchiveSizeLimitExceededException();
            }
        }
    }
}
