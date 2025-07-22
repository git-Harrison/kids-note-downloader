# 📸 KidsNote 알림장 이미지 아카이버

**KidsNote Image Archiver**는 키즈노트(KidsNote)의 알림장 기능을 통해 업로드된 이미지를 자동으로 수집, 변환, 정리해주는 고급 Node.js 기반 CLI 도구입니다.  
본 스크립트는 **교사(teacher)** 계정이 업로드한 이미지 중 특정 날짜 범위(시작날짜 ~ 현재날짜)에 해당하는 자료를 필터링하고,  
모든 이미지를 `.webp` 포맷으로 고효율 저장합니다.

---

## 🔧 주요 기능

| 기능                  | 설명 |
|-----------------------|------|
| ✅ **자동 수집**       | KidsNote REST API를 통해 실시간 데이터 수집 |
| 🧑‍🏫 **교사 글 필터링** | `author.type === "teacher"` 조건 충족 시만 다운로드 |
| 🖼 **이미지 전용 저장** | 텍스트, 영상 제외 후 이미지만 필터링 |
| 📅 **날짜별 정리**     | 업로드 날짜 기준 `YYYY-MM-DD` 폴더 생성 |
| 💾 **파일명 표준화**   | `2025-07-22-1.webp`, `2025-07-22-2.webp` 등 자동 넘버링 |
| 🗜 **고효율 압축 포맷** | `.webp` 포맷, 80% 품질로 저장 (저용량/고화질) |
| 🔐 **세션 쿠키 인증**  | 사용자 제공 `COOKIE` 기반 비공식 API 인증 지원 |

---

## 📁 디렉터리 구조 예시

```text
kids_note_images/
├── 2025-07-01/
│   ├── 2025-07-01-1.webp
│   └── 2025-07-01-2.webp
├── 2025-07-02/
│   └── 2025-07-02-1.webp
...
```

---

## ⚙️ 환경 설정 (.env)

스크립트를 실행하기 전, 실행 디렉토리에 `.env` 파일을 생성하고 아래와 같은 형식으로 환경변수를 입력하세요:

```
KIDSNOTE_API_URL=https://www.kidsnote.com/api/...
KIDSNOTE_COOKIE=여기에_붙여넣은_세션쿠키
KIDSNOTE_BASE_DIR=kids_note_images
KIDSNOTE_START_DATE=원하는 날짜 ex)2025-07-01
```

> 쿠키는 반드시 로그인된 브라우저의 개발자 도구(F12) > 네트워크(Network) 탭에서 `Request Headers` 내 `cookie` 항목을 복사하여 붙여넣으세요.  
> `START_DATE`는 다운로드를 시작할 기준 날짜입니다.

---

## 🧰 설치 및 실행 방법 (비개발자용)

### ✅ Step 1. Node.js 설치

1. [Node.js 공식 사이트](https://nodejs.org/) 방문
2. 운영체제에 맞는 LTS 버전 다운로드 후 설치

### ✅ Step 2. 파일 다운로드 및 압축 해제

- 개발자로부터 제공받은 `.zip` 파일을 원하는 위치에 압축 해제합니다.

### ✅ Step 3. 터미널(명령 프롬프트) 실행

- Windows의 경우 `시작 > cmd` 또는 `PowerShell` 실행
- `cd` 명령어로 폴더 이동 (예: `cd Downloads/kids-note-downloader`)

### ✅ Step 4. 패키지 설치

```bash
npm install
```

> 오류 발생 시 Node.js가 정상 설치되었는지 확인하세요.

### ✅ Step 5. `.env` 파일 생성

- 텍스트 편집기로 `.env` 파일을 새로 만들고, 위의 [환경 설정] 예시를 참고해 값 입력 후 저장

### ✅ Step 6. 스크립트 실행

```bash
node kidsNoteDownloader.js
```

> 실행되면 폴더가 자동으로 생성되고 이미지가 날짜별로 저장됩니다.

---

## 📄 구성 파일 설명

| 파일명                 | 설명 |
|------------------------|------|
| `kidsNoteDownloader.js`| 메인 실행 스크립트 |
| `package.json`         | 의존성 및 메타정보 관리 파일 |
| `package-lock.json`    | 재현 가능한 빌드를 위한 종속성 고정 파일 |
| `.env`                 | 민감한 정보(API URL, 쿠키 등) 보관용 설정 파일 |

---

## ⚠️ 유의사항

- 이 도구는 **비공식 자동화 스크립트**로, KidsNote 측의 변경에 따라 동작이 중단될 수 있습니다.
- 수집한 이미지는 개인 백업 용도로만 사용해야 하며, 제3자에게 공유하거나 상업적으로 이용할 수 없습니다.
- 개발자 도구(F12)를 활용해 쿠키 및 API URL을 직접 추출하는 과정을 요구합니다.

---

## 🧑‍💻 개발자 정보

> **개발자:** harrison (wognsl305@naver.com)  
> **버전:** 1.0.0  
> GitHub: [https://github.com/git-Harrison/kids-note-downloader](https://github.com/git-Harrison/kids-note-downloader)