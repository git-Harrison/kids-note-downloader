# 키즈노트 다운로더 빌드 → windows\out\
#   KidsNoteDownloader.exe  (프로그램)
#   키즈노트 다운로더 설치.msi  (지인에게 보낼 설치 파일)
#   키즈노트 다운로더 설치.zip  (카톡 등 msi 전송이 막힐 때)
$ErrorActionPreference = 'Stop'
$d = $PSScriptRoot
$version = '1.0.0'
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $d 'out'; New-Item -ItemType Directory -Force $out | Out-Null

if (-not (Test-Path "$d\app.ico")) { & "$d\make-icon.ps1" }

# 1) 프로그램
$exe = Join-Path $out 'KidsNoteDownloader.exe'
& $csc @('/nologo', '/target:winexe', '/codepage:65001', '/optimize+', "/out:$exe", "/win32icon:$d\app.ico",
  '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', '/r:Microsoft.VisualBasic.dll',
  "$d\KidsNote.cs")
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# 2) 설치 화면에 보여 줄 안내문 (RTF, 한글은 \uN? 로)
$notice = @'
키즈노트 다운로더 v{0}

키즈노트 알림장·앨범의 사진과 동영상을 원본 그대로 내 PC에 저장하는 개인용 프로그램이에요.

• 키즈노트 공식 프로그램이 아니에요. 키즈노트가 바뀌면 동작이 멈출 수 있어요.
• 받은 사진은 우리 가족 보관용으로만 써 주세요. 다른 아이가 함께 나온 사진은 밖에 공유하지 마세요.
• 로그인은 키즈노트 공식 로그인 화면에서 해요. 비밀번호는 이 프로그램에 저장되지 않아요.
• 관리자 권한 없이 내 계정에만 설치돼요. 지울 때는 설정 > 앱 > 설치된 앱에서 지우면 돼요.

아래 '동의합니다'를 체크하고 [설치]를 누르세요.
'@ -f $version
$sb = New-Object Text.StringBuilder '{\rtf1\ansi\deff0{\fonttbl{\f0 Malgun Gothic;}}\fs18 '
foreach ($ch in $notice.Replace("`r", '').ToCharArray()) {
  $c = [int]$ch
  if ($ch -eq "`n") { [void]$sb.Append('\par ') }
  elseif ($ch -in '\', '{', '}') { [void]$sb.Append('\' + $ch) }
  elseif ($c -lt 128) { [void]$sb.Append($ch) }
  else { [void]$sb.Append('\u' + $(if ($c -gt 32767) { $c - 65536 } else { $c }) + '?') }
}
[void]$sb.Append('}')
[IO.File]::WriteAllText("$d\notice.rtf", $sb.ToString(), [Text.Encoding]::ASCII)

# 3) MSI (WiX 3 — %LOCALAPPDATA%\wix314 에 없으면 공식 배포본을 받아 둠)
$wix = Join-Path $env:LOCALAPPDATA 'wix314'
if (-not (Test-Path "$wix\light.exe")) {
  [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
  $zip = Join-Path $env:TEMP 'wix314-binaries.zip'
  Invoke-WebRequest 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip' -OutFile $zip -UseBasicParsing
  Expand-Archive $zip $wix -Force
}
$obj = Join-Path $out 'installer.wixobj'
$msi = Join-Path $out '키즈노트 다운로더 설치.msi'
Push-Location $d
try {
  & "$wix\candle.exe" -nologo -ext WixUIExtension -ext WixUtilExtension "-dVersion=$version" -out $obj installer.wxs
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  & "$wix\light.exe" -nologo -ext WixUIExtension -ext WixUtilExtension -cultures:ko-KR -sice:ICE91 -spdb -out $msi $obj
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
} finally { Pop-Location }
Remove-Item $obj -ErrorAction SilentlyContinue

# 4) zip
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipOut = Join-Path $out '키즈노트 다운로더 설치.zip'
if (Test-Path $zipOut) { Remove-Item $zipOut -Force }
$z = [IO.Compression.ZipFile]::Open($zipOut, 'Create', [Text.Encoding]::UTF8)
[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $msi, [IO.Path]::GetFileName($msi), 'Optimal')
$z.Dispose()
exit 0
