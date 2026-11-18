# 다봐 / ohmyplayer 0.1.17

## PikPak 메뉴 중복 제거

- PikPak 전용 메뉴 하나만 표시하고, 동일한 PikPak WebDAV 연결을 내 저장소 목록에서 중복 노출하지 않습니다.
- 표시 이름이 달라도 WebDAV 주소가 `dav.mypikpak.com`이면 PikPak 연결로 판별합니다.
- 같은 주소를 여러 이름으로 저장한 경우에도 PikPak 전용 메뉴는 실제 PikPak 주소 연결을 우선 사용합니다.
