// RimWorld 1.7 (.NET 10): System.Reflection.MethodInvoker (new in .NET 8) collides with HarmonyLib.MethodInvoker
// in every file that imports both namespaces (CS0104). On net48 the name could only mean Harmony's type, so this
// alias keeps the meaning of every existing use.
global using MethodInvoker = HarmonyLib.MethodInvoker;
