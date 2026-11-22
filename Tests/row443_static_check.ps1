param([string]$dll,[string]$achtung,[string]$cecil,[string]$mutate="",[string]$mutOut="")
# Static IL check for row 443 (no game boot). Prints PASS/FAIL lines; exit 1 on any FAIL.
Add-Type -Path $cecil
$fail = 0
function Chk($ok,$msg){ if($ok){ "PASS $msg" } else { "FAIL $msg"; $script:fail++ } }
function Load($p){ $rp = New-Object Mono.Cecil.DefaultAssemblyResolver; $rp.AddSearchDirectory((Split-Path $p)); $rp.AddSearchDirectory("S:\Rimworld MP shiz\lab\game"); $rp.AddSearchDirectory("S:\Rimworld MP shiz\waylith\backup_16_20261013\game\Mods\761219125\Assemblies"); foreach($d in (Get-ChildItem "S:\Rimworld MP shiz\waylith\_lanes\fix1116\laboff\w443\wt3\Source\obj\Release\PublicizedAssemblies" -Directory)){ $rp.AddSearchDirectory($d.FullName) }; $rp.AddSearchDirectory("S:\Rimworld MP shiz\waylith\_lanes\fix1116\laboff\w443\wt3\References"); $pr = New-Object Mono.Cecil.ReaderParameters; $pr.AssemblyResolver=$rp; [Mono.Cecil.AssemblyDefinition]::ReadAssembly($p,$pr) }

# 1. names reflected by the compat exist in the Achtung dll the co-op pack ships
$ach = Load $achtung
$types = @{}; foreach($t in $ach.MainModule.GetTypes()){ $types[$t.FullName]=$t }
$fw = $types["AchtungMod.ForcedWork"]; $fj = $types["AchtungMod.ForcedJob"]
Chk ($fw -ne $null -and $fj -ne $null) "Achtung has ForcedWork and ForcedJob"
$m = $fw.Methods | ? { $_.Name -eq "AllForcedJobs" -and -not $_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.IsArray }
Chk ($m -ne $null) "ForcedWork.AllForcedJobs() instance, array result"
$m = $fw.Methods | ? { $_.Name -eq "WorldComponentTick" -and $_.Parameters.Count -eq 0 }
Chk ($m -ne $null) "ForcedWork.WorldComponentTick()"
$m = $fw.Methods | ? { $_.Name -eq "get_Instance" -and $_.IsStatic }
Chk ($m -ne $null) "ForcedWork.Instance getter static"
$m = $fj.Methods | ? { $_.Name -eq "ExpandJob" -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq "System.Int32" }
Chk ($m -ne $null) "ForcedJob.ExpandJob(int)"
$f = $fj.Fields | ? { $_.Name -eq "pawn" -and $_.FieldType.FullName -eq "Verse.Pawn" -and -not $_.IsStatic }
Chk ($f -ne $null) "ForcedJob.pawn : Pawn"
$f = $fj.Fields | ? { $_.Name -eq "cancelled" -and $_.FieldType.FullName -eq "System.Boolean" }
Chk ($f -ne $null) "ForcedJob.cancelled : bool"
$f = $fj.Fields | ? { $_.Name -eq "cellRadius" -and $_.FieldType.FullName -eq "System.Int32" }
Chk ($f -ne $null) "ForcedJob.cellRadius : int"

# 2. the compat dll
$asm = Load $dll
$ac = $asm.MainModule.GetTypes() | ? { $_.FullName -eq "Multiplayer.Compat.Achtung" }
function Meth($n){ $ac.Methods | ? { $_.Name -eq $n } | select -First 1 }
function Consts($m){ $m.Body.Instructions | ? { $_.OpCode.Code -eq "Ldc_I4" -or $_.OpCode.Code -eq "Ldc_I4_S" -or $_.OpCode.Name -like "ldc.i4.*" } }
function ConstVal($i){ if($i.OpCode.Name -eq "ldc.i4.s" -or $i.OpCode.Name -eq "ldc.i4"){ [int]$i.Operand } else { [int]($i.OpCode.Name.Substring(7)) } }
$post = Meth "PostWorldComponentTick"
$step = Meth "ExpandForcedJobsStep"
$tick = Meth "PatchWorldTick"
Chk ($post -ne $null -and $step -ne $null) "Achtung.PostWorldComponentTick and ExpandForcedJobsStep exist"
if($post -ne $null -and $step -ne $null){
  $ins = @($post.Body.Instructions)
  $rowCalls = $ins | ? { $_.OpCode.Name -eq "call" -and $_.Operand.Name -eq "Row" -and $_.Operand.DeclaringType.Name -eq "LabOff" }
  Chk (@($rowCalls).Count -eq 1) "tick step calls LabOff.Row once"
  $rowIdx = [array]::IndexOf($ins, ($rowCalls | select -First 1))
  $v = ConstVal $ins[$rowIdx-1]
  Chk ($v -eq 443) "LabOff.Row argument is 443 (got $v)"
  $stepCall = $ins | ? { $_.OpCode.Name -eq "call" -and $_.Operand.Name -eq "ExpandForcedJobsStep" } | select -First 1
  Chk ($stepCall -ne $null -and [array]::IndexOf($ins,$stepCall) -gt $rowIdx) "ExpandForcedJobsStep is called after the row gate"
  $mpCall = $ins | ? { $_.OpCode.Name -like "call*" -and $_.Operand.Name -eq "get_IsInMultiplayer" } | select -First 1
  Chk ($mpCall -ne $null -and [array]::IndexOf($ins,$mpCall) -lt $rowIdx) "MP.IsInMultiplayer checked before the row gate"
  $interval = $ins | ? { $_.OpCode.Name -like "ldc.i4*" } | % { ConstVal $_ } | ? { $_ -ne 443 -and $_ -ne 0 }
  Chk (@($interval) -contains 15) "tick interval constant 15 present"
  $sins = @($step.Body.Instructions)
  $inv = $sins | ? { $_.OpCode.Name -eq "callvirt" -and $_.Operand.Name -eq "Invoke" -and $_.Operand.DeclaringType.Name -eq "MethodBase" }
  Chk (@($inv).Count -ge 2) "step invokes reflected methods (AllForcedJobs, ExpandJob)"
  $stfld = $sins | ? { $_.OpCode.Name -eq "ldsfld" -and $_.Operand.Name -eq "expandJobMethod" }
  Chk ($stfld -ne $null) "step uses expandJobMethod"
  $b = $sins | ? { $_.OpCode.Name -like "ldc.i4*" } | % { ConstVal $_ }
  Chk (@($b) -contains 4) "step budget constant 4 present"
  $push = $sins | ? { $_.OpCode.Name -eq "call" -and $_.Operand.Name -eq "PushState" }
  Chk ($push -ne $null) "step seeds Rand (PushState)"
  # no local-only state anywhere in the two methods
  $bad = @(); foreach($mm in @($post,$step)){ foreach($i in $mm.Body.Instructions){ if($i.Operand -is [Mono.Cecil.MethodReference]){ $dt=$i.Operand.DeclaringType.FullName; if($dt -like "UnityEngine.Time" -or $dt -like "UnityEngine.Camera*" -or $dt -like "UnityEngine.Application" -or $dt -like "Verse.Find" -and $i.Operand.Name -like "*Camera*" -or $dt -like "RimWorld.Planet.WorldRendererUtility" -or $dt -like "System.Diagnostics.Stopwatch" -or $dt -like "System.DateTime"){ $bad += "$dt::$($i.Operand.Name)" } } } }
  Chk ($bad.Count -eq 0) "no Time/Camera/Application/Stopwatch/DateTime calls in the tick step $($bad -join ',')"
}
Chk ($tick -ne $null -and (@($tick.Body.Instructions | ? { $_.Operand -is [string] -and $_.Operand -eq "PostWorldComponentTick" }).Count -eq 1)) "PatchWorldTick registers the postfix"
$pre = Meth "PreFrameExpander"
$pi = @($pre.Body.Instructions | % { $_.OpCode.Name }) -join ' '
Chk ($pi -match "call.*|ldc.i4.0") "PreFrameExpander still present (per-frame path skipped in MP)"
$ci = @($pre.Body.Instructions | ? { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq "get_IsInMultiplayer" })
Chk ($ci.Count -eq 1) "PreFrameExpander reads MP.IsInMultiplayer"

# optional mutation (planted wrong value): rewrite the Row argument or the budget in a copy
if($mutate -ne ""){
  $ins = $post.Body.Instructions
  if($mutate -eq "row"){ $rc = $ins | ? { $_.OpCode.Name -eq "call" -and $_.Operand.Name -eq "Row" } | select -First 1; $i = $ins[$ins.IndexOf($rc)-1]; $i.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4; $i.Operand=[int]444 }
  if($mutate -eq "budget"){ foreach($i in $step.Body.Instructions){ if($i.OpCode.Name -eq "ldc.i4.4"){ $i.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4_0; break } } }
  $asm.Write($mutOut); "MUTATED $mutate -> $mutOut"; exit 0
}
"RESULT fails=$fail"
if($fail -gt 0){ exit 1 } else { exit 0 }
