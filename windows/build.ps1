# 키즈노트 다운로더 빌드 → windows\out\
#   KidsNoteDownloader.exe       (프로그램)
#   키즈노트 다운로더 설치.exe     (지인에게 보낼 설치 파일 — 요즘 앱 스타일 설치 화면)
#   키즈노트 다운로더 설치.zip     (카톡 등 exe 전송이 막힐 때)
$ErrorActionPreference = 'Stop'
$d = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $d 'out'; New-Item -ItemType Directory -Force $out | Out-Null
if (-not (Test-Path "$d\app.ico") -or -not (Test-Path "$d\icon.png")) { & "$d\make-icon.ps1" }
$refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:Microsoft.CSharp.dll'

# 1) 프로그램
$exe = Join-Path $out 'KidsNoteDownloader.exe'
& $csc (@('/nologo', '/target:winexe', '/codepage:65001', '/optimize+', "/out:$exe", "/win32icon:$d\app.ico") + $refs +
  @('/r:System.Web.Extensions.dll', '/r:Microsoft.VisualBasic.dll', '/r:System.Management.dll', "$d\KidsNote.cs", "$d\Install.cs"))
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# 2) 설치 파일 (프로그램을 품고 있음)
$setup = Join-Path $out '키즈노트 다운로더 설치.exe'
& $csc (@('/nologo', '/target:winexe', '/codepage:65001', '/optimize+', "/out:$setup", "/win32icon:$d\app.ico",
  "/resource:$exe,KidsNoteDownloader.exe", "/resource:$d\icon.png,icon.png") + $refs +
  @("$d\Setup.cs", "$d\Install.cs", "$d\ModernSetup.cs"))
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# 3) zip
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = Join-Path $out '키즈노트 다운로더 설치.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
$z = [IO.Compression.ZipFile]::Open($zip, 'Create', [Text.Encoding]::UTF8)
[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $setup, [IO.Path]::GetFileName($setup), 'Optimal')
$z.Dispose()
Get-ChildItem $out -Filter '*.msi' | Remove-Item -ErrorAction SilentlyContinue
exit 0
