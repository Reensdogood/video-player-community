# 다봐 / ohmyplayer 0.1.16

## PikPak WebDAV 연결 개선

- WebDAV의 비동기 XML 응답 처리 설정을 수정해 PikPak 연결 확인 중 발생하던 `Set XmlReaderSettings.Async to true` 오류를 해결했습니다.
- WebDAV 주소가 PikPak 서버이면 연결 이름을 비워도 PikPak으로 저장하고, 이름이 `WebDAV`여도 메인 메뉴에서 PikPak 연결로 인식합니다.
- 메인 메뉴의 PikPak 항목은 저장된 WebDAV 연결을 직접 엽니다. 연결이 없으면 PikPak 주소와 설정 안내가 채워진 저장소 설정을 열고, 연결을 저장한 뒤 목록으로 이어집니다.
- PikPak 연결 상태를 메인 메뉴에 표시합니다.
