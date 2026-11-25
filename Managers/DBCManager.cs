using DBCD;
using DBCD.IO;
using DBCD.Providers;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using wow.tools.local.Providers;
using static TACTSharp.RootInstance;

namespace wow.tools.local.Managers
{
    public class DBCManager(IDBDProvider dbdProvider, IDBCProvider dbcProvider, IEnumProvider enumProvider) : IDBCManager
    {
        private readonly DBDProvider dbdProvider = (DBDProvider)dbdProvider;
        private readonly DBCProvider dbcProvider = (DBCProvider)dbcProvider;
        private readonly EnumProvider enumProvider = (EnumProvider)enumProvider;

        private DBCD.DBCD? dbcd;
        private static readonly Lock dbcdLock = new();

        private MemoryCache Cache = new(new MemoryCacheOptions() { SizeLimit = 250 });
        private readonly ConcurrentDictionary<(string Name, string Build, bool UseHotfixes, LocaleFlags Locale), LockEntry> Locks = [];

        private sealed class LockEntry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int ReferenceCount { get; set; }
        }

        private LockEntry GetLockEntry((string Name, string Build, bool UseHotfixes, LocaleFlags Locale) key)
        {
            while (true)
            {
                var entry = Locks.GetOrAdd(key, _ => new LockEntry());

                lock (entry)
                {
                    if (Locks.TryGetValue(key, out var registeredEntry) && ReferenceEquals(entry, registeredEntry))
                    {
                        entry.ReferenceCount++;
                        return entry;
                    }
                }
            }
        }

        private void ReleaseLockEntry((string Name, string Build, bool UseHotfixes, LocaleFlags Locale) key, LockEntry entry)
        {
            lock (entry)
            {
                entry.ReferenceCount--;

                if (entry.ReferenceCount == 0)
                    Locks.TryRemove(new(key, entry));
            }
        }

        public async Task<IDBCDStorage> GetOrLoad(string name, string build)
        {
            return await GetOrLoad(name, build, false);
        }

        public async Task<IDBCDStorage> GetOrLoad(string name, string build, bool useHotfixes = false, LocaleFlags locale = LocaleFlags.All_WoW, List<int>? pushIDFilter = null)
        {
            if (locale != LocaleFlags.All_WoW)
            {
                return await LoadDBC(name, build, useHotfixes, locale);
            }

            if (pushIDFilter != null)
            {
                return await LoadDBC(name, build, useHotfixes, locale, pushIDFilter);
            }

            var cacheKey = (Name: name, Build: build, UseHotfixes: useHotfixes, Locale: locale);

            if (Cache.TryGetValue(cacheKey, out var cachedDBC))
                return (DBCD.IDBCDStorage)cachedDBC!;

            var lockEntry = GetLockEntry(cacheKey);
            var lockAcquired = false;

            try
            {
                await lockEntry.Semaphore.WaitAsync();
                lockAcquired = true;

                if (!Cache.TryGetValue(cacheKey, out cachedDBC))
                {
                    // Key not in cache, load DBC
                    Console.WriteLine("DBC " + name + " for build " + build + " (hotfixes: " + useHotfixes + ") is not cached, loading!");
                    cachedDBC = await LoadDBC(name, build, useHotfixes, locale);
                    Cache.Set(cacheKey, cachedDBC, new MemoryCacheEntryOptions().SetSize(1));
                }
            }
            finally
            {
                if (lockAcquired)
                    lockEntry.Semaphore.Release();

                ReleaseLockEntry(cacheKey, lockEntry);
            }

            return (IDBCDStorage)cachedDBC!;
        }

        private async Task<IDBCDStorage> LoadDBC(string name, string build, bool useHotfixes = false, LocaleFlags locale = LocaleFlags.All_WoW, List<int>? pushIDFilter = null)
        {
            if (locale != LocaleFlags.All_WoW)
            {
                dbcProvider.localeFlags = locale;
            }

            if (dbcd == null)
            {
                lock (dbcdLock)
                {
                    // check again when acquiring the lock
                    if (dbcd == null)
                    {
                        // we don't feed enumProvider to DBCD for now
                        if (dbdProvider.isUsingBDBD)
                            dbcd = new DBCD.DBCD(dbcProvider, DBDProvider.GetBDBDStream());
                        else
                            dbcd = new DBCD.DBCD(dbcProvider, dbdProvider);
                    }
                }
            }

            var storage = dbcd.Load(name, build);

            dbcProvider.localeFlags = locale;

            var splitBuild = build.Split('.');

            if (splitBuild.Length != 4)
            {
                throw new Exception("Invalid build!");
            }

            var buildNumber = uint.Parse(splitBuild[3]);

            if (!useHotfixes)
                return storage;

            if (HotfixManager.hotfixReaders.Count == 0)
                await HotfixManager.LoadCaches();

            if (HotfixManager.hotfixReaders.TryGetValue(buildNumber, out HotfixReader? hotfixReaders))
            {
                // DBCD PR #17 support
                if (pushIDFilter != null)
                {
                    storage.ApplyingHotfixes(hotfixReaders, (row, shouldDelete) =>
                    {
                        if (!pushIDFilter.Contains(row.PushId))
                            return RowOp.Ignore;

                        return HotfixReader.DefaultProcessor(row, shouldDelete);
                    });
                }
                else
                {
                    storage.ApplyingHotfixes(hotfixReaders);
                }
            }

            return storage;
        }

        public void ClearCache()
        {
            Cache.Dispose();

            lock (dbcdLock)
            {
                // we don't feed enumProvider to DBCD for now
                if (dbdProvider.isUsingBDBD)
                    dbcd = new DBCD.DBCD(dbcProvider, DBDProvider.GetBDBDStream());
                else
                    dbcd = new DBCD.DBCD(dbcProvider, dbdProvider);
            }

            Cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 250 });
        }

        public void ClearHotfixCache()
        {
            // TODO: Only clear hotfix caches? :(
            Cache.Dispose();
            Cache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 250 });
        }

        public string[] GetDBCNames(string? build = null)
        {
            var dbcNames = dbdProvider.GetNames();

            if (!string.IsNullOrEmpty(build))
            {
                var filteredNames = new List<string>();
                foreach (var name in dbcNames)
                {
                    if (dbcProvider.DB2IsCached(name, build))
                        filteredNames.Add(name);
                }
                return filteredNames.ToArray();
            }
            else
            {
                return dbcNames;
            }
        }

        public async Task<List<DBCDRow>> FindRecords(string name, string build, string col, int val, bool single = false)
        {
            var rowList = new List<DBCDRow>();

            var storage = await GetOrLoad(name, build);
            if (col == "ID")
            {
                if (storage.TryGetValue(val, out var row))
                {
                    foreach (var fieldName in storage.AvailableColumns)
                    {
                        if (fieldName != col)
                            continue;

                        // Don't think FKs to arrays are possible, so only check regular value
                        if (row[fieldName].ToString() == val.ToString())
                        {
                            rowList.Add(row);
                            if (single)
                                return rowList;
                        }
                    }
                }
            }
            else
            {
                foreach (var row in storage.Values)
                {
                    foreach (var fieldName in storage.AvailableColumns)
                    {
                        if (fieldName != col)
                            continue;

                        // Don't think FKs to arrays are possible, so only check regular value
                        if (row[fieldName].ToString() == val.ToString())
                        {
                            rowList.Add(row);
                            if (single)
                                return rowList;
                        }
                    }
                }
            }

            return rowList;
        }
    }
}
