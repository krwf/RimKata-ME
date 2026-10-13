$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$harmony = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077\1.6\Assemblies\0Harmony.dll'
[Reflection.Assembly]::LoadFrom($harmony) | Out-Null
Add-Type -Path (Join-Path $PSScriptRoot 'ValidateME.cs') -ReferencedAssemblies $harmony, 'System.Core.dll'
exit [MEWrapperValidation]::Run($repo, (Split-Path -Parent $harmony))
