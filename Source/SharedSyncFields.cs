using System;
using System.Collections.Generic;
using Multiplayer.API;

namespace Multiplayer.Compat
{
    /// <summary>
    /// One registration per type + field across all compat modules (27 Nov 2026, FIXES row 469 class).
    /// Multiplayer's Sync.RegisterSyncField adds the field to a Dictionary with Add (Multiplayer Source/Client/Syncing/Sync.cs:74),
    /// so a second module that registers the same type + field throws on load. Both Nice Bill Tab (row 283) and Dubs Mint Menus
    /// (row 469) watch Bill.suspended and are both in the co-op pack. Modules that watch a vanilla field another module may also
    /// watch get it here. No-game check: waylith/_lanes/fix1116/tools/dup_syncfield_check.py on the Source folder.
    /// </summary>
    internal static class SharedSyncFields
    {
        private static readonly Dictionary<string, ISyncField> fields = new Dictionary<string, ISyncField>();
        private static readonly object fieldsLock = new object();

        public static ISyncField Get(Type type, string fieldName)
        {
            var key = type.FullName + "/" + fieldName;
            lock (fieldsLock)
            {
                if (!fields.TryGetValue(key, out var field))
                {
                    field = MP.RegisterSyncField(type, fieldName);
                    fields[key] = field;
                }
                return field;
            }
        }
    }
}
