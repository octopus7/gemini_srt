# 에이전트 메모
- 모든 사용자 응답은 한국어로 제공합니다.
- `GeminiSrtTranslator.App`은 Gemini API `generateContent`를 지원하는 모델(기본값 `gemini-1.5-flash`)로 SRT 자막을 번역합니다.
- 앱 시작 시 저장된 API 키가 있으면 모델 목록을 `ListModels` 호출로 불러와 드롭다운에 표시하고, 선택한 모델을 기억합니다.
- 번역 호출 시 JSON 포맷 출력을 강제하여 자막 인덱스별 결과를 매핑합니다.
- 사용자가 입력한 Gemini API 키와 모델 선택은 `AppData/Local/GeminiSrtTranslator/settings.json`에 저장합니다.
