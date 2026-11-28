using System;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Giddy-Up 2 fork (memegoddess.giddyup): the float menu lambdas FullL1_1 registers (AddMountingOptions 0-3) share one closure
    /// (pawn, animal, pawnData). MP validation failed on GiddyUp.ExtendedPawnData (no sync worker), so the mount / dismount options threw
    /// from the UI. Register a worker that sends the pawn and reads the pawn's ExtendedPawnData on the other side.</summary>
    [MpCompatFor("memegoddess.giddyup")]
    public class GiddyUpForkSyncWorkerV
    {
        private static Func<Pawn, object> getData;
        private static Func<object, Pawn> getPawn;

        public GiddyUpForkSyncWorkerV(ModContentPack mod)
        {
            try
            {
                var type = AccessTools.TypeByName("GiddyUp.ExtendedPawnData");
                var getter = AccessTools.PropertyGetter(type, "Pawn");
                var util = AccessTools.Method(AccessTools.TypeByName("GiddyUp.StorageUtility"), "GetExtendedPawnData");
                if (type == null || getter == null || util == null)
                    throw new Exception("ExtendedPawnData.Pawn or StorageUtility.GetExtendedPawnData not found");
                getPawn = o => (Pawn)getter.Invoke(o, null);
                getData = p => util.Invoke(null, new object[] { p });
                MP.RegisterSyncWorker<object>(SyncExtendedPawnData, type);
                Log.Message("[MPCompat - FULL1127 V] Giddy-Up 2 fork: ExtendedPawnData sync worker registered");
            }
            catch (Exception e)
            {
                Log.Error("[MPCompat - FULL1127 V] Giddy-Up 2 fork: ExtendedPawnData sync worker not registered: " + e.Message);
            }
        }

        private static void SyncExtendedPawnData(SyncWorker sync, ref object data)
        {
            if (sync.isWriting)
                sync.Write(data == null ? null : getPawn(data));
            else
            {
                var pawn = sync.Read<Pawn>();
                data = pawn == null ? null : getData(pawn);
            }
        }
    }
}
