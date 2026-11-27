using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
	/// <summary>OnlyIfToleranceBelow by csh1668</summary>
	/// <see href="https://github.com/csh1668/OnlyIfToleranceBelow"/>
	/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3088210509"/>
	[MpCompatFor("seohyeon.onlyIfToleranceBelow")]
	internal class OnlyIfToleranceBelow
	{
		// Current mod builds keep the value in a ConditionalWeakTable (1.7) or a Prepatcher field (older),
		// so DrugPolicyEntry has no real field to sync. The mod exposes Get_/Set_OnlyIfToleranceBelow instead.
		private static MethodInfo getTolerance;
		private static MethodInfo setTolerance;
		private static float[] before;

		public OnlyIfToleranceBelow(ModContentPack mod)
		{
			var exposed = AccessTools.TypeByName("DrugPolicyEnhanced.ExposedPrepatcherFields");
			getTolerance = AccessTools.Method(exposed, "Get_OnlyIfToleranceBelow", [typeof(DrugPolicyEntry)]);
			setTolerance = AccessTools.Method(exposed, "Set_OnlyIfToleranceBelow", [typeof(DrugPolicyEntry), typeof(float)]);

			MP.RegisterSyncMethod(typeof(OnlyIfToleranceBelow), nameof(SyncedSetTolerance));

			MpCompat.harmony.Patch(AccessTools.Method(typeof(Dialog_ManageDrugPolicies), nameof(Dialog_ManageDrugPolicies.DoPolicyConfigArea)),
				prefix: new HarmonyMethod(typeof(OnlyIfToleranceBelow), nameof(PreDoPolicy)) { priority = Priority.First },
				postfix: new HarmonyMethod(typeof(OnlyIfToleranceBelow), nameof(PostDoPolicy)) { priority = Priority.Last });
		}

		private static float Get(DrugPolicyEntry entry) => (float)getTolerance.Invoke(null, [entry]);

		static void PreDoPolicy(Dialog_ManageDrugPolicies __instance)
		{
			if (!MP.IsInMultiplayer)
				return;

			DrugPolicy policy = __instance.SelectedPolicy;
			if (policy == null)
			{
				before = null;
				return;
			}

			before = new float[policy.Count];
			for (int i = 0; i < before.Length; i++)
				before[i] = Get(policy[i]);
		}

		static void PostDoPolicy(Dialog_ManageDrugPolicies __instance)
		{
			var old = before;
			before = null;
			if (!MP.IsInMultiplayer || old == null)
				return;

			DrugPolicy policy = __instance.SelectedPolicy;
			if (policy == null || policy.Count != old.Length)
				return;

			for (int i = 0; i < old.Length; i++)
			{
				float now = Get(policy[i]);
				if (now == old[i])
					continue;

				// Undo the local write, then apply it on every client through the sync.
				setTolerance.Invoke(null, [policy[i], old[i]]);
				SyncedSetTolerance(policy, i, now);
			}
		}

		private static void SyncedSetTolerance(DrugPolicy policy, int index, float value)
		{
			if (policy == null || index < 0 || index >= policy.Count)
				return;
			setTolerance.Invoke(null, [policy[index], value]);
		}
	}
}
