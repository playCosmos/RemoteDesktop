# 구조 개선 계획

개인용 원격 데스크톱의 현재 코드는 한 프로세스 안에 서버와 클라이언트를 두고, 화면·입력·클립보드만 옮기는 구조다. 이번 조사에서 호출처가 없는 공유 폴더 API를 제거했다. 아래는 동작을 바꾸지 않고 구조를 나누는 순서다.

## 이번에 지운 것

호출 관계를 소스, 테스트, Android 클라이언트까지 대조했다.

| 제거 | 이유 |
|---|---|
| GET/PUT /v1/files | Windows·Android 화면이 호출하지 않음. 파일 이동은 클립보드 배치만 사용 |
| RemoteClient.Files/Upload/Download, RemoteFile | 위 API 전용. 통합 테스트만 호출 |
| RemoteApi.files/upload/download | Android에서 정의만 있고 호출 없음 |
| HostSettings.Folder, 숨긴 HostFolder 입력란 | 공유 폴더 경로를 저장하려고만 남아 있었음 |
| GET /v1/health, RemoteClient.CheckServer | 클라이언트와 테스트가 호출하지 않음. 세션 유지는 /v1/ping |
| Desktop.Capture | CaptureDetailed만 사용 |

기존 settings.json에 Folder가 있어도 역직렬화 때 무시되고, 다음 저장부터 빠진다. 클립보드 캐시, OPAQUE 등록, 모니터 캡처, 입력 주입은 그대로다.

설치 서비스, 트레이, UAC 헬퍼는 소스에 남아 있지 않다. obj 아래 InstallOptionsDialog 생성물은 이전 빌드 찌꺼기이며 소스가 아니다.

## 지금 구조의 문제

앱 코드 자체는 작다. Windows 소스 약 3,000줄, Android 약 1,200줄이다. 문제는 파일이 역할보다 기능 역사를 따라 커진 점이다.

- MainWindow.xaml.cs 약 590줄이 서버 수명, 접속, 프레임 루프, 키보드 후킹, 클립보드, 전체화면을 한 클래스에 둔다. 포인터 좌표를 고치면 단축키와 비밀번호 저장까지 같은 파일을 건드리게 된다.
- RemoteHost.cs가 Kestrel 구성, 세션, OPAQUE, 클립보드 HTTP, 프레임 HTTP를 한 메서드 안에서 등록한다. 라우트가 늘 때마다 인증 미들웨어와 잠금 순서를 같이 읽어야 한다.
- Desktop.cs가 DXGI 스케줄, JPEG, 커서, SendInput을 같이 가진다. 캡처 지연과 입력 좌표는 실패 원인이 다른데 수정 지점이 같다.
- Android MainActivity.java 약 770줄이 레이아웃 생성, 제스처, 재접속, 메뉴를 한 파일에 둔다. 가로 모드 배치를 바꾸면 세션 복구 코드를 같이 보게 된다.
- 프로토콜 레코드가 C#과 Java에 각각 있다. 필드 이름 하나는 컴파일로 잡히지 않고 통합 테스트의 Windows 경로에서만 보인다.
- tests/IntegrationChecks/Program.cs 하나가 비밀번호, 캡처, 클립보드, 세션 만료를 순서대로 실행한다. 실패한 검사의 원인이 앞선 세션 상태에 남는다.

## 순서

한 단계가 끝나기 전에는 다음 단계의 파일을 나누지 않는다. 각 단계는 기존 통합 테스트를 그대로 통과해야 한다.

1. 호스트 라우트 분리. RemoteHost는 포트, 인증서, 세션 토큰, 수명만 남긴다. 로그인·OPAQUE, 프레임·입력, 클립보드를 각각 작은 맵 메서드로 옮긴다. URL, 상태 코드, 잠금 순서는 바꾸지 않는다. 이 단계는 반영되어 있다. StartAsync는 미들웨어와 수명만 두고, 등록은 MapAuthentication, MapClipboard, MapDesktop에 있다.
2. 캡처와 입력 분리. Desktop의 공개 표면인 Displays, CaptureDetailed, Apply, Release는 유지하고, JPEG·뷰포트·화질 적응은 캡처 파이프라인으로, SendInput은 입력 주입기로 나눈다. 좌표 계산 MapPoint는 캡처 비트맵과 입력 주입이 같은 함수를 쓰므로 그대로 공유한다.
3. Windows 화면 분리. MainWindow는 탭과 상태 표시만 남긴다. 서버 시작·중지·비밀번호는 HostSession, 프레임 루프·후킹·클립보드는 ClientSession으로 옮긴다. XAML 컨트롤 이름과 사용자에게 보이는 문구는 유지한다.
4. Android 세션 분리. MainActivity의 접속, 프레임 스케줄, 백그라운드 ping, 재로그인을 RemoteSession으로 옮긴다. 뷰 생성과 제스처는 Activity에 둔다. 다른 앱에서 돌아왔을 때의 재접속 조건은 코드를 옮기기 전에 테스트로 고정할 수 없으므로, 이동 전후 로그 문구와 타임아웃 값(약 5초 ping, 메모리 비밀번호)을 그대로 복사한다.
5. 프로토콜 계약. docs/PROTOCOL.md의 필드 이름을 Windows 통합 테스트가 JSON으로 한 번씩 읽고 쓰게 한다. Java 쪽은 같은 이름의 요청을 만드는 작은 단위 테스트를 추가하기 전에는 RemoteApi를 재작성하지 않는다. 언어를 넘는 공용 라이브러리는 만들지 않는다.
6. 통합 테스트 분할. 지금 Program.cs의 순서를 비밀번호, 세션, 클립보드, 캡처 네 개의 실행으로 나누되, 각 실행은 자신의 임시 디렉터리와 포트를 연다. 검사 항목은 줄이지 않는다.

하지 않을 일: 설치 서비스와 트레이를 다시 넣기, 공유 폴더 API를 호환용으로 되돌리기, 화면 코덱을 이 분리 작업과 같이 바꾸기, MainWindow를 MVVM 프레임워크로 한 번에 교체하기.

## 완료 기준

라우트와 클래스를 나눈 뒤에도 통합 테스트가 비밀번호 재시작, 세션 1개 제한, 클립보드 해시·개정, 모니터 좌표, 끊김 줄이기 재사용을 통과해야 한다. Windows 클라이언트 포인터는 프레임과 같은 정규화 좌표를 쓰고, Android는 현재 뷰포트만 요청한다. 이 두 동작이 분리 과정에서 파일 경계를 넘기면 그 단계의 완료로 보지 않는다.
