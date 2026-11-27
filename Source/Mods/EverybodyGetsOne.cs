using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Everybody Gets One by Memegoddess</summary>
    /// <remarks>
    /// FIXES row 505: CounterClipboardPaste.AddBillAndPasteCounter calls the synced BillStack.AddBill, which adds the bill on every PC
    /// from a copy, and then sets the person counter on the LOCAL bill object only (from this PC's clipboard). In Multiplayer the call
    /// is replaced by one synced call that adds the bill and sets the counter, from the clipboard serialized as XML, on every PC.
    /// </remarks>
    [MpCompatFor("memegoddess.everybodygetsone")]
    class EverybodyGetsOne
    {
        static FieldInfo clipboardField;
        static MethodInfo saveAsString, loadFromString, setPersonCounter, cloneForUseSingle;

        public EverybodyGetsOne(ModContentPack mod)
        {
            if (LabOff.Row(505))
            {
                Log.Message("[MPCompat - Everybody Gets One] row 505 switched off by the lab, counter paste not synced");
                return;
            }
            var paste = AccessTools.TypeByName("Everybody_Gets_One.CounterClipboardPaste");
            var clip = AccessTools.TypeByName("Everybody_Gets_One.CounterClipboard");
            var ext = AccessTools.TypeByName("Everybody_Gets_One.MapCompExtensions");
            var script = AccessTools.TypeByName("TD_Find_Lib.ScribeXmlFromString");
            var search = AccessTools.TypeByName("TD_Find_Lib.QuerySearch");
            if (paste == null || clip == null || ext == null || script == null || search == null)
            {
                Log.Error("[MPCompat - Everybody Gets One] row 505: types not found, counter paste can desync");
                return;
            }
            clipboardField = AccessTools.Field(clip, "Clipboard");
            saveAsString = AccessTools.Method(script, "SaveAsString");
            loadFromString = AccessTools.Method(script, "LoadFromString")?.MakeGenericMethod(search);
            setPersonCounter = AccessTools.Method(ext, "SetPersonCounter");
            cloneForUseSingle = AccessTools.Method(search, "CloneForUseSingle");
            var target = AccessTools.Method(paste, "AddBillAndPasteCounter");
            if (clipboardField == null || saveAsString == null || loadFromString == null || setPersonCounter == null || cloneForUseSingle == null || target == null)
            {
                Log.Error("[MPCompat - Everybody Gets One] row 505: members not found, counter paste can desync");
                return;
            }
            MpCompat.harmony.Patch(target, prefix: new HarmonyMethod(typeof(EverybodyGetsOne), nameof(Prefix)));
            MP.RegisterSyncMethod(typeof(EverybodyGetsOne), nameof(SyncedAddAndPaste)).ExposeParameter(1).CancelIfAnyArgNull();
            Log.Message("[MPCompat - Everybody Gets One] row 505: Everybody Gets One counter paste synced");
        }

        static bool Prefix(BillStack stack, Bill bill)
        {
            if (!MP.IsInMultiplayer) return true;
            var clipboard = clipboardField.GetValue(null);
            if (clipboard == null || !(bill is Bill_Production))
            {
                stack.AddBill(bill);   // synced by Multiplayer, nothing to paste
                return false;
            }
            string xml = saveAsString.Invoke(null, new[] { clipboard }) as string;
            if (string.IsNullOrEmpty(xml)) stack.AddBill(bill);
            else SyncedAddAndPaste(stack, bill, xml);
            return false;
        }

        // Runs on every PC: BillStack.AddBill runs directly inside the command, then the counter is set on the bill that was added here.
        static void SyncedAddAndPaste(BillStack stack, Bill bill, string xml)
        {
            stack.AddBill(bill);
            var production = bill as Bill_Production;
            if (production == null) return;
            var loaded = loadFromString.Invoke(null, new object[] { xml, new object[0] });
            if (loaded == null) return;
            var counter = cloneForUseSingle.Invoke(loaded, new object[] { production.Map, null });
            setPersonCounter.Invoke(null, new[] { production, counter });
        }
    }
}
