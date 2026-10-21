[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script = Join-Path $repo 'tools/Update-Version.ps1'
$fixture = Join-Path $repo ('artifacts/verification/version-smoke-' + [Guid]::NewGuid().ToString('N'))
$dir = Join-Path $fixture 'src/PeerDesk.Windows'
[IO.Directory]::CreateDirectory($dir) | Out-Null
$versionPath = Join-Path $dir 'Version.props'
$source = Join-Path $dir 'Example.cs'
$encoding = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($source, 'class Example {}', $encoding)
[IO.File]::WriteAllText($versionPath, '<Project><PropertyGroup><Version>0.1.0-preview.3</Version><AssemblyVersion>0.1.0.0</AssemblyVersion><FileVersion>0.1.0.3</FileVersion><InformationalVersion>0.1.0-preview.3</InformationalVersion><SourceFingerprint></SourceFingerprint></PropertyGroup></Project>', $encoding)
$watch = [Diagnostics.Stopwatch]::StartNew()
$count = 0
function Check($value,$name) { if (!$value) { throw $name }; $script:count++; Write-Output ('PASS ' + $name) }
function Version { return ([xml][IO.File]::ReadAllText($versionPath)).Project.PropertyGroup }
function Update { & powershell.exe -NoProfile -File $script -ProjectRoot $fixture; Check ($LASTEXITCODE -eq 0) 'version process exits successfully' }
Update
Check ((Version).Version -eq '0.1.0-preview.3') 'first source fingerprint retains assigned version'
$hash = (Version).SourceFingerprint
Update
Check ((Version).Version -eq '0.1.0-preview.3' -and (Version).SourceFingerprint -eq $hash) 'unchanged source does not consume a version'
[IO.File]::AppendAllText($source, '// change one', $encoding)
& powershell.exe -NoProfile -File $script -ProjectRoot $fixture -CheckOnly
Check ($LASTEXITCODE -eq 3 -and (Version).Version -eq '0.1.0-preview.3') 'check-only detects change without mutation'
Update
Check ((Version).Version -eq '0.1.0-preview.4' -and (Version).FileVersion -eq '0.1.0.4' -and (Version).InformationalVersion -eq '0.1.0-preview.4') 'source change increments preview and all displayed/file metadata together'
[IO.Directory]::CreateDirectory((Join-Path $dir 'bin')) | Out-Null
[IO.File]::WriteAllText((Join-Path $dir 'bin/ignored.cs'), 'ignored generated output', $encoding)
Update
Check ((Version).Version -eq '0.1.0-preview.4') 'build outputs do not change product version'
[IO.File]::AppendAllText($source, '// concurrent change', $encoding)
$args = @('-NoProfile','-File',('"' + $script + '"'),'-ProjectRoot',('"' + $fixture + '"'))
$a = Start-Process powershell.exe -ArgumentList $args -WindowStyle Hidden -PassThru
$b = Start-Process powershell.exe -ArgumentList $args -WindowStyle Hidden -PassThru
Check ($a.WaitForExit(10000) -and $b.WaitForExit(10000)) 'parallel version updates finish within a finite timeout'
Check ($a.ExitCode -eq 0 -and $b.ExitCode -eq 0) 'parallel version processes succeed'
Check ((Version).Version -eq '0.1.0-preview.5') 'parallel builds consume exactly one version per source fingerprint'
Check ((Version).AssemblyVersion -eq '0.1.0.0') 'compatible assembly binding version remains stable'
$xml = [xml][IO.File]::ReadAllText($versionPath)
$xml.Project.PropertyGroup.Version = '0.2.0-preview.1'
$xml.Save($versionPath)
Update
Check ((Version).Version -eq '0.2.0-preview.1' -and (Version).FileVersion -eq '0.2.0.1' -and (Version).AssemblyVersion -eq '0.2.0.0' -and (Version).InformationalVersion -eq '0.2.0-preview.1') 'manual version assignment repairs all metadata without an extra increment'
Write-Output ('PASS total=' + $count + ' elapsedMs=' + $watch.ElapsedMilliseconds)
