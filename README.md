# 🧸 KidsNote Downloader (Unofficial)

**⚠️ 본 도구는 KidsNote의 *비공식 API*를 활용한 개인용 이미지 다운로드 도우미입니다. 공식적으로 지원되지 않으며, 서비스 정책 변경에 따라 정상 동작하지 않을 수 있습니다.**

---

## ✅ 개요

어린이집 알림장/앨범에 등록된 사진들을 **자동으로 다운로드**하여, 날짜별로 정리하고 `.webp` 포맷으로 저장해주는 간단한 프로그램입니다.

---

## 💻 설치 및 사용법 (개발지식 없어도 됨)

### 1. 준비물 설치

- 윈도우 사용자라면 [Node.js 설치하기](https://nodejs.org/ko) (설치 후 재부팅 추천)
- 압축 파일 받은 폴더에서 `Shift + 마우스 오른쪽 클릭 → 여기에서 PowerShell 창 열기`

### 2. Git 클론 or 다운로드

```bash
git clone https://github.com/your-id/kidsnote-downloader.git
cd kidsnote-downloader
```

혹은 ZIP 다운로드 후 압축 해제

### 3. 의존 패키지 설치

```bash
npm install
```

### 4. `.env` 파일 만들기

`.env`라는 이름의 새 파일을 생성하고, 아래 내용을 복사해서 붙여넣으세요:

```env
KIDSNOTE_API_URL=https://www.kidsnote.com/api/v1_2/children/4442113/reports/?page_size=5000&tz=Asia%2FSeoul&center_id=51810&cls=1140413&child=4442113
KIDSNOTE_COOKIE=_gcl_au=... (한 줄 전체 입력, 공백 없이)
KIDSNOTE_BASE_DIR=kids_note_images
KIDSNOTE_START_DATE=2025-01-01
```

- **KIDSNOTE_API_URL**: 알림장 or 앨범 API 주소
- **KIDSNOTE_COOKIE**: F12 → Application → Cookie 복사해서 붙여넣기
- **KIDSNOTE_BASE_DIR**: 저장될 폴더 이름
- **KIDSNOTE_START_DATE**: 다운로드 시작 기준일 (예: 2025-01-01)

### 5. 실행하기

```bash
node kidsNoteDownloader.js
```

성공 시 날짜별 폴더에 `.webp` 확장자로 이미지 저장됨 🎉

---

## 📄 구성 파일 설명

| 파일명                 | 설명 |
|------------------------|------|
| `kidsNoteDownloader.js`| 메인 실행 스크립트 |
| `package.json`         | 의존성 및 메타정보 관리 파일 |
| `package-lock.json`    | 재현 가능한 빌드를 위한 종속성 고정 파일 |
| `.env`                 | API URL, 쿠키 등 민감 정보 별도 저장용 환경설정 파일 |

---

## 📜 라이선스

```
MIT License

Copyright (c) 2025 harrison

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission no[LICENSE](kids_note_downloader%2FLICENSE)tice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 🙋 문의

- **Author**: harrison
- **Email**: wognsl305@naver.com
