# 다봐 · ohmyplayer (Windows PoC)

한국어 제품명은 **다봐**, 영문 제품명과 Windows 실행 파일명은 **ohmyplayer**입니다. Windows x64용 영상 플레이어 PoC로, 로컬 영상과 접근 가능한 직접 미디어 URL을 재생하고 외부/내장 자막과 오디오 트랙을 선택할 수 있습니다.

## 현재 동작 범위

- 로컬 파일 열기, 여러 파일 재생목록, 개별 항목 제거와 재생목록 전체 비우기, 파일 끌어 놓기
- HTTP/HTTPS 직접 미디어 URL 재생 및 파일 저장
- YouTube, Vimeo, X, Instagram, Facebook, Threads URL은 Windows WebView2 창에서 각 서비스의 웹 플레이어를 엽니다. 로그인은 해당 창에서 사용자가 직접 할 수 있습니다.
- LibVLC가 지원하는 형식과 코덱 재생 (컨테이너/코덱/파일 상태에 따라 달라질 수 있음)
- 별도 영상 창의 재생/일시정지/정지, 탐색, 볼륨(숫자 표시), 음소거, 이전/다음. 재생 중 마우스를 움직이거나 터치하면 파일 선택과 컨트롤이 나타나며, 비활성 상태에서는 자동으로 숨습니다.
- 영상 끝에서 재생을 누르면 처음부터 다시 재생하며, Ctrl+C로 현재 프레임을 PNG 캡처합니다. 캡처 위치는 설정에서 고르거나 첫 캡처 때 지정합니다.
- 내장 오디오/자막 트랙 선택 및 SRT/VTT/ASS/SSA 등 외부 자막 추가
- 영상과 같은 폴더에 같은 파일명으로 둔 자막 자동 검색/선택
- 로컬 영상과 WebDAV 영상의 CC 생성을 지원합니다. WebDAV 영상은 사용자가 `CC 생성`을 누르면 전체 파일을 임시 폴더에 받은 뒤 Whisper로 분석합니다. 영상 크기만큼 임시 디스크 공간과 네트워크 시간이 필요합니다. Whisper 실행 엔진, small 모델, FFmpeg는 번들되어 Python이나 모델 경로 입력이 필요 없습니다. 생성 자막은 Ollama 또는 Gemini 자동 번역 설정이 켜져 있으면 이어서 번역합니다.
- 작은 영상 선택/설정 메인 창과 별도 영상 창. Windows 기본 제목표시줄은 숨기고 앱 내부 창 이동·최소화·닫기 동작을 제공합니다.
- 별도 영상 창의 창 모드, 전체화면, 항상 위에 표시되는 PIP 전환
- 설정 화면에서 Ollama 로컬 모델 목록을 읽고 메타데이터와 미검증 범위를 표시
- SMB 공유(UNC)와 WebDAV 연결 저장, 폴더 목록 탐색, 영상 재생
- PikPak Premium WebDAV 연결 지원. PikPak 설정에서 발급한 전용 자격 증명을 저장소 탭에 입력
- Google Drive, OneDrive, Dropbox는 설정의 **동기화 폴더 선택**으로 PC 동기화 앱이 관리하는 폴더를 저장하고 앱 안에서 하위 폴더·영상 목록을 탐색할 수 있습니다. 이 방식은 공급자의 직접 API 연결이 아니라 로컬 동기화 폴더 접근입니다. 온라인 전용 항목은 공급자 앱의 다운로드/자리표시자 정책과 네트워크 상태에 따라 재생이 달라질 수 있습니다. 브라우저 로그인 기반 직접 API 목록 조회·재생 relay 코드도 포함하지만 PoC에는 제품 OAuth 앱 등록값이 없으므로 로그인은 제공되지 않습니다.
- Naver MYBOX 동기화 폴더 연결과 Open API 연결: PC 동기화 폴더 탐색 또는 개인 액세스 토큰 확인, 파일/폴더 목록 탐색, 임시 다운로드 URL을 통한 영상 재생 경로
- Gemini API 번역: 설정에서 사용 여부, 모델, 대상 언어를 고르고 개인 API 키를 Windows 사용자 계정으로 보호해 저장합니다. 같은 폴더·이름의 SRT/VTT, 직접 연 SRT/VTT, CC 생성 자막을 전체 번역한 뒤 별도 자막 트랙으로 추가합니다. [Gemini API 연결 준비 가이드](https://reensdogood.github.io/video-player-community/GEMINI_API_SETUP_GUIDE.html)도 설정에서 열 수 있습니다.

## 아직 제공하지 않는 기능

NFS, 번역 TTS, 소셜 플랫폼 페이지에서 영상 파일 추출/다운로드, Naver DRM 콘텐츠 재생은 제공하지 않거나 실계정 검증이 끝나지 않았습니다. Gemini 번역은 사용자가 제공한 API 키와 네트워크 연결이 필요하며 Google 사용량/요금 정책이 적용됩니다. 번역 대상은 SRT/VTT 자막 텍스트이고, 내장 자막 트랙과 웹 플레이어·온라인 영상 자막은 지원하지 않습니다. YouTube/Vimeo/SNS URL은 서비스 자체 웹 플레이어로 재생하며 사이트 정책, 로그인, 게시자 임베드 설정에 따라 재생이 제한될 수 있습니다. URL 다운로드는 HTTP/HTTPS 직접 파일 주소만 대상으로 합니다. YouTube API 정책은 사전 서면 승인 없는 다운로드와 오프라인 재생을 금지합니다. Vimeo는 영상 소유자 설정과 API 권한이 다운로드를 허용하는 경우만 파일 접근을 제공합니다. DRM 또는 접근 제한을 우회하지 않습니다. Google Drive/OneDrive/Dropbox OAuth 경로는 코드에 구현됐지만 제품 개발자 앱 등록과 계정 실측을 마치지 않았습니다. MYBOX는 공식 Open API의 개인 액세스 토큰으로 연결하며, 토큰은 사용자가 MYBOX 설정에서 생성해야 합니다. 암호 폴더와 공유받은 폴더는 MYBOX Open API 지원 범위가 아닙니다. Ollama 모델 선택은 설정에 저장되지만 현재 번역 파이프라인에는 연결되지 않습니다.

Gemini 번역은 자막을 24개 구간씩 나눠 전체 처리합니다. 긴 자막은 여러 API 요청으로 처리돼 준비 시간이 늘고 사용량이 발생합니다. 번역은 원본 SRT/VTT를 덮어쓰지 않으며 번역본을 별도 자막 트랙으로 추가합니다. 번들 CC 엔진은 CPU 모드로 동작하며 긴 영상은 준비 시간이 길 수 있습니다. 실시간 ASR과 번역 TTS는 미지원입니다.

## 실행

요구 사항: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 빌드

```powershell
dotnet build .\VideoPlayer.Wpf\VideoPlayer.Wpf.csproj -c Release
```

### 포터블 배포 폴더 생성

```powershell
dotnet publish .\VideoPlayer.Wpf\VideoPlayer.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\artifacts\windows-x64
```

`artifacts\windows-x64` 폴더 전체를 복사한 뒤 `ohmyplayer.exe`를 실행합니다. 런타임과 LibVLC 네이티브 파일이 함께 게시되는 구성입니다.

### Windows 설치 파일과 업데이트 패키지 생성

```powershell
.\scripts\Build-WindowsInstaller.ps1 -Version 0.1.17
```

`artifacts/windows-installer/0.1.17`에 사용자별 설치용 `beondiueyow-ohmyplayer-win-Setup.exe`, `beondiueyow-ohmyplayer-win.msi`, 포터블 ZIP, 업데이트 패키지와 릴리스 피드가 생성됩니다. 이 패키지에는 CC 실행 엔진, 모델, FFmpeg가 함께 포함돼 설치 파일이 약 1GB에 이를 수 있습니다. 기존 설치본의 업데이트 연결을 유지하기 위해 내부 업데이트 식별자는 그대로 둡니다. 앱은 매 시작 때 업데이트를 확인하며 메인 화면에서 수동 확인도 할 수 있습니다. 실제 업데이트를 받게 하려면 생성된 파일을 GitHub 릴리스에 게시해야 합니다. 현재 설치 파일은 서명되지 않아 Windows 게시자 경고가 보일 수 있습니다.

CPU용 CC 런타임을 새로 만들 때는 Python과 FFmpeg가 준비된 Windows 빌드 PC에서 `scripts\Build-CaptionRuntime.ps1`을 먼저 실행합니다. Whisper small 모델은 빌드 때 자동으로 내려받아 설치 파일에 포함됩니다.

첫 배포의 지원 범위와 아직 검증되지 않은 기능은 [현재 한계 및 배포 범위](docs/KNOWN_LIMITATIONS.md)에 정리했습니다.

### 저장소 및 클라우드 연결

앱의 **설정 → 저장소**에서 SMB UNC 경로(`\\server\share`) 또는 WebDAV URL을 추가합니다. SMB는 Windows의 네트워크 인증을 사용하거나 입력한 자격 증명을 연결에 사용합니다. WebDAV/SMB 암호와 클라우드 갱신 토큰은 현재 Windows 사용자 계정의 DPAPI로 보호해 저장합니다.

일반 사용자에게 Client ID 발급을 요구하지 않습니다. 현재 PoC는 Google Drive·OneDrive·Dropbox의 **동기화 폴더 선택**으로 실제 연결·목록 조회를 지원합니다. 계정 로그인으로 공급자 API를 직접 조회하는 경로는 ohmyplayer 운영자가 제품 OAuth 등록과 권한 검토를 완료한 뒤 활성화할 수 있습니다. 등록값은 `cloud-providers.json`의 앱 식별값으로 공급하며 사용자 입력란을 만들지 않습니다. 데스크톱 앱에는 Client Secret을 넣지 않으며 시스템 브라우저 OAuth + PKCE를 사용합니다.

- Google Drive: Desktop 앱 OAuth 클라이언트, `drive.readonly` 범위. 테스트 모드에서는 계정을 테스트 사용자로 추가해야 할 수 있습니다.
- OneDrive: Microsoft Entra 앱 등록에서 모바일/데스크톱 플랫폼과 위임 `Files.Read` 권한을 허용합니다. 로그인 리디렉션은 `http://localhost` 루프백입니다.
- Dropbox: Scoped app, `files.metadata.read` 및 `files.content.read` 권한. OAuth 설정에서 public client/PKCE를 허용하고, 허용 리디렉션 URI에 `http://localhost:53682/oauth/callback/`을 등록합니다.
- PikPak: Premium 계정에서 WebDAV를 켜고 별도 WebDAV 계정을 만들어 WebDAV 연결로 등록합니다. Connected Apps 및 데이터 전송 쿼터가 적용됩니다.

MYBOX 연결은 MYBOX 웹의 **설정 → 계정 및 개인 액세스 토큰 관리**에서 사용자가 토큰을 발급해 앱에 한 번 붙여넣는 방식입니다. 토큰은 30/60/90/180일 만료로 생성할 수 있고 생성 직후에만 확인 가능하므로 DPAPI로 암호화해 저장합니다. 토큰은 MYBOX 파일 권한을 가지므로 타인과 공유하지 않아야 합니다. 암호 폴더와 공유받은 폴더는 MYBOX Open API가 지원하지 않습니다. [MYBOX Open API 시작하기](https://developers.mybox.naver.com/getting-started), [루트 파일/폴더 목록](https://developers.mybox.naver.com/docs/dms_root), [파일 다운로드 주소](https://developers.mybox.naver.com/docs/files_download), [PikPak WebDAV 안내](https://pikpakdrive.com/en-US/faq)

## 설정 데이터

모델 선택과 연결 정보는 `%LOCALAPPDATA%\ohmyplayer\settings.json`에 저장됩니다. WebDAV 암호와 OAuth 갱신 토큰은 현재 Windows 사용자 계정의 DPAPI로 보호합니다. 기존 PoC의 `%LOCALAPPDATA%\MoraePlayer\settings.json`이 있으면 처음 설정을 열 때 새 위치로 옮깁니다.

## 아이콘

사용자가 제공한 플레이 화면을 참고해 중앙 재생 기호를 앱 아이콘으로 제작했습니다. 리소스 원본은 `VideoPlayer.Wpf\Assets\ohmyplayer.png`, Windows 아이콘은 `VideoPlayer.Wpf\Assets\ohmyplayer.ico`입니다.

## 의존성 및 배포 검토

- [LibVLCSharp.WPF 3.10.2](https://www.nuget.org/packages/LibVLCSharp.WPF/3.10.2)
- [VideoLAN.LibVLC.Windows 3.0.24](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.24)
- [VideoLAN LibVLC](https://www.videolan.org/vlc/libvlc.html) 프로젝트는 LGPL 2.1 라이선스를 안내합니다.

재배포 전에 정확한 네이티브 바이너리/의존성 라이선스와 LGPL 의무, 고지문 및 교체 가능성 요건을 검토해야 합니다. 이 PoC 저장소는 아직 최종 배포용 법률 검토나 라이선스 선언을 완료하지 않았으며 `LICENSE` 파일을 포함하지 않습니다.

## 프로젝트 문서

- [Community 범위](docs/COMMUNITY_SCOPE.md)
- [현재 한계 및 배포 범위](docs/KNOWN_LIMITATIONS.md)
- [0.1.1 릴리스 노트](docs/RELEASE_NOTES_0.1.1.md)
- [0.1.12 릴리스 노트](docs/RELEASE_NOTES_0.1.12.md)
- [0.1.13 릴리스 노트](docs/RELEASE_NOTES_0.1.13.md)
- [0.1.14 릴리스 노트](docs/RELEASE_NOTES_0.1.14.md)
- [0.1.15 릴리스 노트](docs/RELEASE_NOTES_0.1.15.md)
- [0.1.16 릴리스 노트](docs/RELEASE_NOTES_0.1.16.md)
- [0.1.17 릴리스 노트](docs/RELEASE_NOTES_0.1.17.md)
