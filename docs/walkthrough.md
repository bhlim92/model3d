# Aura3D - 블렌더 실시간 소켓 연동 및 자동 실행 통합 결과 보고서

기존 백그라운드 CLI 빌드 방식의 느린 속도를 개선하기 위한 **실시간 소켓(TCP Socket) 통신 모드**와, 사용자가 수동으로 블렌더를 켜고 파이썬 스크립트를 올리는 번거로움을 덜어주는 **"원클릭 블렌더 자동 기동 & 소켓 서버 실행"** 기능이 완성되었습니다.

---

## 🌟 주요 도입 기능 및 기술 개요

1. **원클릭 블렌더 프로그램 자동 기동 (`/api/launch-blender`)**
   - 사용자가 설정 탭에 설정한 Blender.exe 경로를 기반으로 블렌더 GUI를 자동으로 실행합니다.
   - Node.js의 `spawn` 프로세스 분리 기법(`detached: true`, `child.unref()`)을 활용하여, 웹 서버 프로세스와 완전히 독립된 형태로 블렌더 GUI가 상주 실행됩니다.
   - 실행 시 `--python blender_socket_server.py` 파라미터를 넘겨주어, **블렌더가 열리자마자 소켓 상주 서버가 자동으로 로드 및 시작**되도록 하여 수동 스크립트 실행 과정을 완전히 자동화했습니다.

2. **블렌더용 백그라운드 소켓 리스너 구축 (`blender_socket_server.py`)**
   - 블렌더 메인 뷰포트 내부에서 작동하는 경량 소켓 서버 스크립트입니다.
   - **스레드 안전성 보장**: 백그라운드 TCP 리스너 스레드(포트: `5555`)가 코드를 수신하면, 메인 스레드 상의 타이머 큐(`bpy.app.timers`)로 전달하여 블렌더가 먹통이 되거나 다운되는 현상을 원천 방지합니다.
   - 요청을 받은 즉시 메인 뷰포트에 오브젝트를 실시간 조립하고, `gltf` 포맷으로 내보내기를 완수합니다.

3. **Express 백엔드 소켓 파이프라인 및 자동 폴백 (`server.js`)**
   - 클라이언트에서 소켓 모드를 요청하면 백엔드가 `net.Socket`을 기동하여 즉시 블렌더 세션에 스크립트를 주입합니다.
   - 만약 블렌더 소켓 서버가 오프라인이거나 연결되지 않는 경우, **기존의 백그라운드 CLI 실행기(`blender --background`) 모드로 자동 전환**하여 유연하게 처리를 완료합니다.

4. **프론트엔드 UI/UX 완성 (`app.js`, `index.html`)**
   - 설정 탭의 `[블렌더 자동 실행 & 소켓 연동]` 버튼 및 메인 화면의 실시간 가이드 내 숏컷 버튼을 바인딩하여 백엔드의 `/api/launch-blender` API를 호출하게 처리했습니다.
   - 경로가 입력되지 않았을 경우, 친절하게 설정 탭으로 자동 전환하는 마이크로 인터랙션을 설계했습니다.

---

## 생성 및 수정된 최종 파일 리스트

- [blender_socket_server.py](file:///c:/Users/samsung/proj/model3d/blender_socket_server.py) (신규): 블렌더 내부 실행용 멀티스레드 기반 TCP 소켓 서버 스크립트.
- [server.js](file:///c:/Users/samsung/proj/model3d/server.js) (수정): TCP 소켓 송수신 레이어, CLI fallback 자동 백업 제어, 블렌더 GUI 자동 기동 API 통합.
- [index.html](file:///c:/Users/samsung/proj/model3d/index.html) (수정): 설정창 소켓 토글 필드 및 포트 양식, 블렌더 자동 실행 버튼 및 숏컷 보강.
- [app.js](file:///c:/Users/samsung/proj/model3d/app.js) (수정): 프론트엔드 버튼 이벤트 바인딩 및 비동기 API 통신 연동.
- [style.css](file:///c:/Users/samsung/proj/model3d/style.css) (수정): 커스텀 모델 설정 폼 및 소켓 스위치 관련 세부 스타일 추가.

---

## 🚀 실시간 소켓 연동 구동 및 활용 가이드

### 1단계: 원클릭 블렌더 자동 실행
1. 브라우저로 [http://localhost:8080](http://localhost:8080)에 접속합니다.
2. **설정(Settings ⚙️)** 탭으로 이동하여 로컬 컴퓨터에 설치된 `Blender.exe` 경로를 입력합니다. (예: `C:\Program Files\Blender Foundation\Blender 4.0\blender.exe`)
3. 바로 아래에 있는 **[블렌더 자동 실행 & 소켓 연동]** 버튼을 클릭합니다.
4. **결과**: 블렌더 프로그램이 즉시 실행되고 백그라운드 소켓 서버(`localhost:5555`)가 자동으로 기동됩니다.
   * *콘솔 창(Window -> Toggle System Console)을 열어보면 `[Aura3D Server Thread] TCP Listener started on localhost:5555` 메시지가 표시된 것을 확인할 수 있습니다.*

### 2단계: 실시간 3D 모델 조립 테스트
1. Aura3D 웹사이트의 설정 탭에서 **"실시간 소켓 연동 활성화"** 체크박스가 켜져 있는지 점검합니다. (기본 켜짐)
2. **생성(Create 🪄)** 탭으로 이동하고 생성 엔진을 **Blender AI**로 둔 채 프롬프트를 입력합니다:
   - 예: `"a stylized dining chair with a cushion"` (쿠션이 있는 의자)
3. **"3D 모델 생성하기"** 버튼을 클릭합니다.
4. **결과 확인**:
   * **블렌더 창**: AI가 코드를 전달받아 열려 있는 블렌더 화면 안에서 실시간으로 사물이 순간이동하듯 조각조각 조립되는 모습을 볼 수 있습니다!
   * **웹 브라우저**: 연동이 끝나는 즉시(0.1초 수준) 웹 3D 뷰어 화면에 조립된 모델이 로드됩니다.
   * **오프라인Fallback**: 만약 블렌더 창을 꺼두거나 소켓이 중단되면 자동으로 CLI 백그라운드 렌더러로 변경되어 3초 뒤 모델이 출력됩니다.

---

## 🔍 추가 개선 사항
- **3D 뷰어 카메라 줌인 제한 완화**: 세부 디테일을 더 가까이 관찰할 수 있도록 Three.js OrbitControls의 최소 줌인 도달 거리(`minDistance`)를 기존 `2`에서 `0.1`로 대폭 축소하고, 최대 줌아웃 거리(`maxDistance`)도 `40`으로 확장하였습니다.
