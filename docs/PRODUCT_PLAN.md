# Product plan: Windows PoC first

Status: planning draft, 2026-11-16

## Delivery sequence

1. Windows x64 PoC: local files, direct authorized URLs, subtitle/audio track selection, playlists, and SMB/WebDAV sources.
2. Windows product: quality, recovery, accessibility, cloud integrations where official APIs permit playback, local/cloud language providers, and signed updates.
3. Android and iOS products: platform-native file access, background playback, hardware decode, app-store and DRM constraints validated independently.

## Media and services

- “All formats” is not a support promise. Publish a tested container/codec matrix and the codec engine's license notices.
- Use official service players/APIs for hosted video. If a service does not permit third-party playback, open its official player or report unsupported.
- Download is available only when the service/API and content owner explicitly authorize it. DRM and access-control bypass are excluded.
- Naver MYBOX and Naver DRM remain feasibility questions pending current official documentation or partner confirmation.

## Cloud media sources

Candidate providers: Google Drive, OneDrive, Dropbox, and Naver MYBOX. A provider adapter handles official OAuth, directory listing, metadata, token renewal, range/seek behavior, and authorized media reads. Credentials stay in platform secure storage. No provider is promised until its official API and playback permission have been verified.

The public edition can expose a provider interface and include only providers selected for Community. Additional or commercial integrations can be distributed separately, subject to each provider's terms.

## Model selection and user disclosures

The settings UI lists available local or cloud models by task: translation, speech recognition (ASR), and speech synthesis (TTS). Each entry must show supported tasks/languages, measured limits, context or audio limits, connectivity, estimated memory, license, cost when applicable, and whether media leaves the device. Unknown capabilities are shown as unverified.

Local models are detected through explicitly configured local runtimes such as Ollama. Discovery does not imply translation, ASR, or TTS support; each capability is probed and benchmarked. Windows PoC starts with subtitle-text translation. ASR-based subtitles and TTS dubbing are separate optional stages.

## Naver DRM verification gate

Verify the exact Naver service/content type, official player or SDK, app/domain restrictions, login support, license server/CDM requirements, and Windows/Android/iOS distribution terms. Outcomes: officially supported playback, official web-player only, partner contract required, unsupported, or unknown. Login alone is not proof that an app can play DRM media. No cookie extraction, stream-key extraction, decryption, or DRM bypass.

## Community and Pro boundary

Community is intended to contain a complete useful local player and the documented extension interfaces. Pro candidates include additional cloud providers, advanced translation/TTS providers and quotas, enterprise management, and commercial support. Pro implementation must remain outside this public repository; compile-time feature flags do not protect source code once it has been published.

The license has not been chosen. Do not redistribute or reuse this source until a `LICENSE` file is added. Dependencies and codec builds require a separate license and patent review.

## Updates

The product UI calls this “App updates.” Builds and signed release assets may be managed through Git hosting. Validate signatures and checksums, explain update endpoints/data in privacy/security documentation, and preserve user settings. Mobile builds follow their app-store update policies.
