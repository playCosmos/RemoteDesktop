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

설치는 같은 실행 파일의 포터블, 사용자별 설치, 모든 사용자 설치다. 모든 사용자 설치만 Windows 서비스로 로그온 뒤 UI를 띄우고, UAC 보안 화면 프레임을 사용자 세션의 캡처 경로에 넘긴다. 창을 닫으면 트레이에 남는다. 머신 설치 제거는 서비스가 `Stopped`로 전이한 뒤에만 서비스 등록을 지운다. 약 8초 안에 멈추지 않으면 제거를 중단하고 기존 방화벽 규칙 복원을 시도한다. 이 정지 정책은 가짜 상태 조회로 통합 검사하지만, 실제 UAC·서비스 설치/제거 시나리오는 Windows 테스트 VM에서 따로 확인해야 한다. obj 아래 InstallOptionsDialog 생성물은 이전 빌드 찌꺼기이며 소스가 아니다.

모든 사용자 설치 업데이트는 서비스 중지 전에 기존 EXE 백업을 만들고, 새 버전을 복사한 다음 서비스가 `Running`으로 확인돼야 완료된다. 서비스 상태를 중지 요청 전에 기록하므로 중지 시간 초과도 다시 정리할 수 있다. SCM 접근에 실패해 상태조차 읽지 못했다면 서비스에 추가 명령을 보내지 않는다. 중간 실패면 새 서비스를 멈추거나 제거하고 이전 EXE·로그인 자동 실행·바로 가기를 복구한 뒤, 원래 실행 중이던 서비스만 다시 시작한다. 롤백이 서비스 정지나 파일 복구에서 실패하면 백업 파일을 지우지 않고 경로를 오류에 표시해 수동 복구를 가능하게 한다.

방화벽 규칙은 Firewall COM 조회를 우선 사용하고, 실패하면 `netsh` 출력으로 확인한다. `netsh` 조회·수정 프로세스는 15초, 비승격 UI가 기다리는 승격된 방화벽 도우미는 2분으로 제한한다. 만료된 하위 프로세스는 종료하고 경고를 기록해 방화벽 오류가 서버 시작을 무기한 멈추지 않게 한다.

사용자별 설치는 큰 EXE 복사만 백그라운드에서 수행해 창을 계속 쓸 수 있다. COM 바로 가기와 자동 실행 레지스트리는 UI의 STA 스레드에서 마무리한다. 창을 종료하면 진행 중인 복사가 끝날 때까지 세션 정리가 기다려 EXE가 반쯤 기록된 채 앱이 끝나지 않게 한다.

설치 폴더 삭제는 PowerShell 도우미가 앱 PID와 시작 시각이 모두 바뀌거나 사라질 때까지 기다린 뒤 `-LiteralPath`로 수행한다. 고정 지연에 기대지 않으므로 서버·클립보드 종료가 오래 걸려도 EXE를 일찍 지우지 않으며, 경로 특수문자도 셸 코드로 해석하지 않는다. 종료 대기와 삭제에는 각각 상한을 두고, 실패는 `%TEMP%\PersonalRemoteDesktop-uninstall.log`에 남긴다.

## 지금 구조의 문제

앱 코드는 비교적 작지만 책임이 한 파일에 모인 곳이 있다. Windows `MainWindow.xaml.cs`는 약 400줄, Android `MainActivity.java`는 약 880줄이다.

- MainWindow.xaml.cs 약 590줄이 서버 수명, 접속, 프레임 루프, 키보드 후킹, 클립보드, 전체화면을 한 클래스에 둔다. 포인터 좌표를 고치면 단축키와 비밀번호 저장까지 같은 파일을 건드리게 된다.
- RemoteHost.cs가 Kestrel 구성, 세션, OPAQUE, 클립보드 HTTP, 프레임 HTTP를 한 메서드 안에서 등록한다. 라우트가 늘 때마다 인증 미들웨어와 잠금 순서를 같이 읽어야 한다.
- Desktop.cs가 DXGI 스케줄, JPEG, 커서, SendInput을 같이 가진다. 캡처 지연과 입력 좌표는 실패 원인이 다른데 수정 지점이 같다.
- Android `MainActivity.java`는 화면 생성·제스처·디코딩·프레임 적용을 맡는다. 접속·입력 큐·ping·인증 재개·프레임 스케줄은 `RemoteSession`으로 나눴다.
- 프로토콜 레코드가 C#과 Java에 각각 있다. 필드 이름 하나는 컴파일로 잡히지 않고 통합 테스트의 Windows 경로에서만 보인다.
- tests/IntegrationChecks/Program.cs가 비밀번호, 세션, 클립보드, 캡처를 한 호스트에서 순서대로 실행했다. 실패한 검사의 원인이 앞선 세션 상태에 남았다. 지금은 각 실행이 자신의 임시 디렉터리와 포트를 연다.

## SecureDesktop 입력 핫패스 (P0-1)

`RemoteHost.StartAsync`는 `settings.json`의 `SecureDesktopEnabled`(기본값 `true`), `PersonalRemoteDesktop` 서비스의 실행 상태, 보안 입력 엔드포인트 존재 여부를 한 번 확인한다. 셋 중 하나라도 없거나 꺼져 있으면 `InputInjector`와 `DisplayCapture`가 보안 데스크톱 경로를 건너뛴다. 설정 파일에서 `SecureDesktopEnabled`를 `false`로 바꾸면 킬 스위치가 된다.

서비스를 사용할 수 있으면 `SecureDesktop`이 350ms 주기로 데스크톱 상태와 최신 보안 프레임을 갱신한다. `InputInjector.ApplyCore`와 `DisplayCapture.SecureReplacement`는 상태 조회에 캐시만 사용하며 입력마다 `Observe`나 보안 프레임 읽기를 실행하지 않는다. 캐시가 `Live`일 때 입력 전달은 기존 파이프를 사용하고 실패를 계측한다. 데스크톱 이름을 확인하지 못하면 입력을 허용하지 않고 `Hidden`으로 유지한다. 일반 화면으로 돌아왔다는 관측은 `OpenInputDesktop` 성공과 비보안 데스크톱 이름으로 확인한다. `Live` 프레임이 2초 넘게 오래되면 `Hidden`으로 전환한다.

보안 화면 상태로 입력을 보류한 횟수는 `SecureDesktop.InputDroppedBySecureDesktop`, 파이프 전달 실패는 `SecureDesktop.InputForwardFailures`에서 확인한다. 각각 최초 발생 때 `Trace` 경고를 남긴다. 전달 실패가 연속 3회면 관측 타이머를 즉시 깨워 데스크톱 상태를 다시 판정한다.

## RemoteHost 라우트/잠금 결합 (P1-1)

세션 만료 타이머는 `gate` 안에서 만료 여부만 확인하고 실제 `Disconnect`를 작업 스레드로 넘긴다. `Disconnect`는 `gate` 안에서 토큰 무효화와 `session.Cancel()`만 수행한다. 클립보드 종료, 입력 해제, 상태 알림은 잠금 밖에서 실행한다. H.264 정리는 `captureGate`를 한 번에 1초만 기다리고, 캡처 요청이 점유 중이면 250ms 뒤 다시 시도한다. `/v1/logout`은 세션 원본을 잠금 안에서 스냅샷한 뒤 잠금 밖에서 종료를 요청하므로 프레임 정리가 인증 상태 잠금을 잡지 않는다.

P0-2에서는 미들웨어가 인증 토큰·세션 원본·취소 토큰을 `HttpContext.Items["auth"]`에 한 번 저장한다. `/v1/input`은 세션 취소를 확인한 뒤 `desktop.Apply`를 `gate` 밖에서 실행하고, 응답할 `notice`가 있을 때만 짧게 세션 원본을 확인한다. 클립보드와 로그아웃도 미들웨어의 인증 결과를 재사용한다. P1-2에서는 `AuthenticationHeaderValue`로 Bearer 헤더를 파싱하고 토큰 길이를 확인한 뒤 UTF-8 바이트를 `FixedTimeEquals`로 비교한다. 입력 요청마다 인증 잠금, `Apply`, 알림 잠금의 평균 시간을 별도 집계하고 256건마다 `Trace`에 기록한다.

P2-1의 비밀번호 로그인 제한은 출처별 `ConcurrentDictionary` 기록을 사용한다. 실패 직후 2초부터 시작해 최대 5분까지 늘리고, 10분 지난 기록은 다음 조회에서 정리한다. 한 출처는 검증 중 한 슬롯만 차지하며 전체 PBKDF2 검증은 CPU 수에 따라 2~4개로 제한한다.

## MainWindow 책임 집중 (P2-3)

`MainWindow.Closing`은 창 숨김·종료 확인만 결정한다. 실제 종료는 닫기를 취소한 뒤 UI 디스패처에서 각 정리 단계를 순서대로 기다리고, 실패한 단계는 기록한 뒤 다음 단계로 넘어간다. 클라이언트 종료는 진행 중인 접속과 연결·클립보드 작업을 취소하고 기다린다. 서버 세션은 시작 작업을 취소·대기한 뒤 호스트와 인증서를 정리한다. 모든 단계를 마치면 `Close()`를 다시 호출한다. `Closed`에는 비동기 정리가 남지 않는다.

P3에서는 Windows `inputs` 채널 용량 64를 유지하고 `TryWrite` 실패 누계와 48개 경고 임계치를 계측한다. Android 입력 큐가 넘치면 연결을 끊지 않고 입력이 2초 동안 없을 때 차단을 해제해 `inputQueueRecovered()`를 호출한다.

## 순서

한 단계가 끝나기 전에는 다음 단계의 파일을 나누지 않는다. 각 단계는 기존 통합 테스트를 그대로 통과해야 한다.

1. 호스트 라우트 분리. RemoteHost는 포트, 인증서, 세션 토큰, 수명만 남긴다. 로그인·OPAQUE, 프레임·입력, 클립보드를 각각 작은 맵 메서드로 옮긴다. URL, 상태 코드, 잠금 순서는 바꾸지 않는다. 이 단계는 반영되어 있다. StartAsync는 미들웨어와 수명만 두고, 등록은 MapAuthentication, MapClipboard, MapDesktop에 있다.
2. 캡처와 입력 분리. Desktop의 공개 표면인 Displays, CaptureDetailed, Apply, Release는 유지하고, JPEG·뷰포트·화질 적응은 캡처 파이프라인으로, SendInput은 입력 주입기로 나눈다. 좌표 계산 MapPoint는 캡처 비트맵과 입력 주입이 같은 함수를 쓰므로 그대로 공유한다. 이 단계는 반영되어 있다. 캡처는 DisplayCapture, 입력은 InputInjector, 둘의 STA 스레드는 DesktopThread다.
3. Windows 화면 분리. MainWindow는 탭과 상태 표시만 남긴다. 서버 시작·중지·비밀번호는 HostSession, 프레임 루프·후킹·클립보드는 ClientSession으로 옮긴다. XAML 컨트롤 이름과 사용자에게 보이는 문구는 유지한다. 이 단계는 반영되어 있다. 버튼 이벤트는 창에 한 줄로 남아 각 세션으로 전달된다.
4. Android 세션 분리. MainActivity의 접속, 프레임 스케줄, 백그라운드 ping, 재로그인을 RemoteSession으로 옮긴다. 뷰 생성과 제스처는 Activity에 둔다. 이 단계는 반영되어 있다. `RemoteSession`이 접속·입력 전송·백그라운드 ping·재인증·재시도·FPS 프레임 스케줄을 맡고, Activity가 H.264/JPEG 디코딩과 화면 적용을 맡는다. 5초 ping, 전경 3회 실패 기준, 3/5초 재시도와 FPS deadline은 정책 검사로 고정했다.
5. 프로토콜 계약. docs/PROTOCOL.md의 필드 이름을 Windows 통합 테스트가 JSON으로 한 번씩 읽고 쓰게 한다. Java 쪽은 같은 이름의 요청을 만드는 작은 단위 테스트를 추가하기 전에는 RemoteApi를 재작성하지 않는다. 언어를 넘는 공용 라이브러리는 만들지 않는다. 이 단계는 반영되어 있다. 로그인, 핑, 모니터, 입력, 클립보드의 필드 이름을 원시 JSON으로 확인한다. 등록되지 않은 시험 호스트는 /v1/pake/start에서 본문을 보기 전에 409를 반환하므로, 성공 응답의 exchangeId, loginResponse, fingerprint, proof는 읽지 않는다. 이 검사를 위해 OPAQUE 등록을 추가하지 않는다.
6. 통합 테스트 분할. Program.cs의 순서를 비밀번호, 세션, 클립보드, 캡처 네 개의 실행으로 나누되, 각 실행은 자신의 임시 디렉터리와 포트를 연다. 검사 항목은 줄이지 않는다. 이 단계는 반영되어 있다. `--capture`가 있을 때만 캡처 실행이 포함된다.

하지 않을 일: 공유 폴더 API를 호환용으로 되돌리기, 화면 코덱을 이 분리 작업과 같이 바꾸기, MainWindow를 MVVM 프레임워크로 한 번에 교체하기.

## 완료 기준

라우트와 클래스를 나눈 뒤에도 통합 테스트가 비밀번호 재시작, 세션 1개 제한, 클립보드 해시·개정, 모니터 좌표, 끊김 줄이기 재사용을 통과해야 한다. Windows 클라이언트 포인터는 프레임과 같은 정규화 좌표를 쓰고, Android는 현재 뷰포트만 요청한다. 이 두 동작이 분리 과정에서 파일 경계를 넘기면 그 단계의 완료로 보지 않는다.
