# Manual Blur Player Architecture

## Status

- 상태: **설계 제안**
- 기준 브랜치: `fix/quality-stabilization-debug`
- 기준 커밋: `37a2717a924a78c3849512fa5905a49bb34239c1`
- 목적: 순수 수동 블러 작업의 프레임 선택/로딩 지연을 제거하고, 사용자가 실제로 본 프레임과 export에서 블러가 적용되는 프레임을 일치시키는 구조 정의
- 적용 범위:
  - **순수 수동 블러**: 새 플레이어형 편집 구조
  - **자동 블러**: 기존 분석 흐름 유지
  - **자동 블러 후 수동 보정**: 기존 frame/PTS 기반 결과 편집 흐름 유지하되, 가능한 범위에서 아래의 프레임 식별 규칙을 공유

---

## 1. 배경

현재 수동 편집은 사용자가 프레임을 선택한 뒤 필요한 프레임을 다시 찾아 디코딩하는 random-access 중심 흐름이다.

확인된 현행 경로는 대략 다음과 같다.

```text
타임라인 클릭
  -> timestamp -> frame ordinal exact 해석
  -> SelectedFrameIndex 변경
  -> frame-index thumbnail 요청
  -> thumbnail을 먼저 await
  -> 80ms debounce
  -> full-resolution exact frame 요청
  -> preview/mask 합성
```

관련 현재 구성:

- `Controls/TimelineFrameStrip.cs`
- `ViewModels/Workspace/FramePreviewViewModel.cs`
- `Services/Video/Session/TimelineController.cs`
- `Services/Video/Session/ExactFrameProvider.cs`
- `Services/Video/TimelineThumbnailProvider.cs`
- `Services/Video/FfFrameExtractor.cs`

특히 `FramePreviewViewModel.OnFrameIndexChanged()`는 저해상도 thumbnail 요청이 끝난 뒤 full-resolution exact frame을 요청한다.

또한 `VideoSession`의 `ExactFrameProvider`와 `TimelineThumbnailProvider`는 같은 `FfFrameExtractor`를 공유한다. 따라서 background timeline thumbnail 생성, PTS 해석, 사용자의 직접 선택 요청이 같은 decoder 자원을 두고 경쟁할 수 있다.

이 구조에서는 uncached VFR 위치나 긴 영상의 먼 구간을 선택할 때 다음 문제가 발생할 수 있다.

- 클릭 후 선택 반응이 늦음
- 로딩 상태가 보이지 않아 클릭이 실패한 것처럼 느껴짐
- 사용자가 재클릭하면서 기존 작업 취소/재시작이 반복될 수 있음
- thumbnail 준비가 exact frame 표시보다 먼저 수행되어 체감 latency가 커짐
- 아직 PTS index가 없는 먼 frame ordinal을 해석할 때 prefix decode 비용이 발생할 수 있음

순수 수동 작업에서는 사용자가 일반적인 동영상 플레이어처럼 영상을 보다가 멈추고 그 화면 위에 블러를 추가하는 것이 더 자연스럽다.

---

## 2. 핵심 결정

### 2.1 순수 수동 모드는 "프레임 이미지 탐색기"가 아니라 "동영상 플레이어 + annotation editor"로 동작한다

순수 수동 모드의 기본 재생 경로는 FFmpeg sequential decode를 사용한다.

```text
FFmpeg sequential decoder
        |
        v
실제로 decode된 현재 frame
        |
        +----> 화면 표시
        |
        +----> FrameIdentity 기록
        |
        +----> 수동 blur overlay 편집
```

Pause 시 현재 화면을 다시 seek/decode하지 않는다.

이미 재생 중 확보한 현재 decoded bitmap을 그대로 고정해서 편집 대상으로 사용한다.

### 2.2 OS 기본 미디어 플레이어와 FFmpeg export decoder를 분리하지 않는다

다음 구조는 사용하지 않는다.

```text
화면 표시: MediaFoundation / AVFoundation / OS player
export: FFmpeg
```

두 decoder가 timestamp rounding, seek landing, reordered frame 처리에서 서로 다른 프레임을 선택할 위험이 있기 때문이다.

겉보기 UX는 일반 플레이어처럼 구성하되, 실제 frame identity는 FFmpeg decode 결과를 기준으로 한다.

### 2.3 `time * fps`로 프레임을 역산하지 않는다

VFR 입력에서는 금지한다.

잘못된 예:

```text
currentTime = 10.000 sec
fps = 30
frame = 300
```

실제 VFR presentation은 예를 들어 다음처럼 될 수 있다.

```text
ordinal 299 -> 9.970 sec
ordinal 300 -> 10.040 sec
```

수동 annotation은 사용자가 실제로 보고 있는 decoded frame의 identity에 연결해야 한다.

---

## 3. FrameIdentity

### 3.1 목표

사용자가 Pause한 실제 프레임과 export 시 처리되는 실제 프레임을 동일하게 식별한다.

### 3.2 제안 데이터

개념상 다음 정보를 가진다.

```csharp
ManualFrameIdentity
{
    int FrameOrdinal;
    long PresentationTimestamp;
    int TimestampOccurrence;
}
```

필드 의미:

- `FrameOrdinal`
  - 순차 decode 기준의 실제 decoded-frame ordinal
  - 현재 mask 시스템과 연결하기 쉬운 기본 key
- `PresentationTimestamp`
  - 실제 decoded frame에서 얻은 presentation timestamp
  - VFR 및 seek 검증에 사용
- `TimestampOccurrence`
  - 동일 PTS가 여러 decoded frame에서 나타나는 경우의 구분값
  - 단일 PTS만으로 프레임을 잘못 매칭하는 것을 방지

현재 `FfFrameExtractor`의 decoded timeline에도 presentation timestamp와 occurrence 개념이 존재하므로 해당 정책과 일치시키는 방향이 적합하다.

### 3.3 저장 원칙

수동 blur annotation은 가능한 경우 다음처럼 저장한다.

```text
FrameIdentity
  + BlurRects 또는 Mask
  + 편집 metadata
```

단순 `double seconds`만 annotation key로 사용하지 않는다.

---

## 4. 순수 수동 모드 재생 구조

### 4.1 진입

수동 workspace 진입 후 즉시 플레이어 decoder를 준비한다.

```text
workspace open
  -> FFmpeg manual-player decoder open
  -> 첫 decoded frame 표시
  -> small frame ring buffer 준비
  -> 재생 가능
```

기존처럼 수동 화면 전체를 처음부터 full-resolution으로 사전 디코딩하지 않는다.

### 4.2 재생

순차적으로 디코딩한다.

```text
ordinal N
  -> decode bitmap
  -> decoded PTS/occurrence 기록
  -> 화면 표시
  -> ring buffer 저장
  -> N+1 decode
```

재생 중 background 작업이 UI frame delivery보다 우선하면 안 된다.

### 4.3 Pause

Pause 요청 시:

1. 현재 display frame을 고정한다.
2. 현재 `FrameIdentity`를 편집 대상 identity로 확정한다.
3. 현재 프레임의 blur annotation을 불러온다.
4. 동일 bitmap 위에 blur overlay를 표시한다.
5. 별도 exact random seek를 다시 수행하지 않는다.

목표는 Pause 직후 편집 화면 전환을 거의 즉시 만드는 것이다.

### 4.4 Resume

현재 frame 다음 ordinal부터 sequential decode를 계속한다.

annotation은 decode stream과 독립적으로 저장되어야 한다.

---

## 5. 프레임 단위 이동

수동 편집에서 좌우 한 프레임 이동은 매우 빈번하므로 small ring buffer를 사용한다.

예:

```text
[N-2] [N-1] [N] [N+1] [N+2]
             ^
           current
```

권장 정책:

- 최근/인접 full-resolution frames만 보관
- frame 개수 고정보다 **byte budget** 기반 관리
- 기본 budget은 구현 시 실제 메모리 사용량 측정 후 결정
- 1080p BGRA는 프레임당 대략 8 MB 수준
- 4K BGRA는 프레임당 대략 32 MB 수준이므로 해상도 무시 고정 개수 캐시는 피한다

동작:

- cache hit: 즉시 표시
- next frame cache miss: sequential decoder에서 다음 frame 하나 디코딩
- previous frame cache miss: 필요한 경우 bounded seek/random decode 또는 ring-buffer 재구성

앞으로 이동하는 작업을 최적 경로로 둔다.

---

## 6. 먼 위치 seek

사용자가 타임라인에서 현재 위치와 먼 곳을 클릭할 때만 seek 기반 흐름을 사용한다.

```text
far seek request
  -> UI 선택 위치 즉시 표시
  -> loading indicator 표시
  -> background low-priority 작업 취소/양보
  -> requested timestamp로 seek
  -> 실제 landing frame decode
  -> FrameIdentity 확정
  -> 화면 교체
  -> 그 위치부터 sequential prefetch 재시작
```

### 6.1 중요 UX 규칙

seek가 오래 걸리더라도 클릭 자체는 즉시 반응해야 한다.

최소 표시:

- 타임라인 선택선 즉시 이동
- 기존 frame은 화면에 유지
- 반투명 loading overlay
- 예: `프레임 불러오는 중...`
- 최신 요청이 이전 요청을 대체했다면 이전 loading request 취소

검은 화면으로 전환한 뒤 기다리게 하지 않는다.

---

## 7. Background prefetch

### 7.1 목적

순수 수동 모드는 화면 진입 후 사용자가 작업하지 않는 동안 다음 이동을 준비한다.

단, prefetch는 사용자 요청 latency를 절대 증가시키면 안 된다.

### 7.2 우선순위

제안 우선순위:

```text
P0  사용자 직접 요청한 현재 frame
P1  현재 frame preview / annotation compose
P2  현재 frame 인접 full-resolution frames
P3  현재 viewport thumbnail
P4  현재 위치 이후 PTS warm-up
P5  먼 구간 thumbnail / 기타 background cache
```

P0/P1 요청이 들어오면 P3~P5는 취소하거나 즉시 양보한다.

### 7.3 진입 직후 prefetch

첫 화면이 표시된 뒤:

```text
current
  -> current+1
  -> current+2
  -> ...
```

방향으로 sequential prefetch한다.

전체 영상을 full-resolution frame bitmap으로 캐싱하지 않는다.

### 7.4 사용자가 먼 곳으로 이동했을 때

기존 background prefetch를 중단하고 새 위치를 중심으로 재시작한다.

```text
기존: 2:00 -> 2:01 -> 2:02 ...
사용자 seek: 20:00
                 |
                 v
기존 prefetch 취소
20:00 frame 우선 decode
20:00 이후 순차 prefetch
```

### 7.5 이동 방향 힌트

최근 사용자 이동이 연속 forward라면 forward prefetch 비중을 높일 수 있다.

최근 2~3회 이동 방향 정도만 사용한다.

복잡한 prediction은 필요하지 않다.

---

## 8. Cache 계층

모든 캐시를 하나로 취급하지 않는다.

### 8.1 PTS / frame identity cache

목적:

- frame ordinal <-> decoded PTS mapping
- VFR 정확성
- export identity 검증

특성:

- bitmap보다 메모리 비용이 작음
- 비교적 넓게 warm-up 가능
- 현재의 decoded PTS timeline resource cap 정책과 충돌하지 않도록 기존 상한을 존중

### 8.2 Timeline thumbnail cache

목적:

- timeline scroll/seek 시 빠른 시각 피드백

특성:

- full-resolution frame보다 작음
- 현재 viewport와 주변 구간 우선
- 먼 thumbnail은 LRU eviction 가능

### 8.3 Full-resolution manual frame cache

목적:

- pause
- 한 프레임 이동
- 반복적인 앞/뒤 확인

특성:

- 가장 비쌈
- 작은 byte-budget LRU/ring buffer
- 전체 영상 캐싱 금지

---

## 9. Annotation 모델

순수 수동 모드에서 화면에 보이는 blur는 원본 영상을 즉시 재인코딩한 결과가 아니다.

편집 중에는 annotation만 저장한다.

예:

```text
ManualBlurAnnotation
{
    FrameIdentity Identity;
    Rect[] BlurRects;
    optional MaskData;
}
```

사용자가 rectangle/brush로 수정하면 해당 frame identity의 annotation만 변경한다.

원본 비디오 파일에는 편집 중 쓰기 작업을 하지 않는다.

---

## 10. Export

최종 저장 시 기존 FFmpeg export pipeline에서 순차 decoded output frame을 기준으로 annotation을 적용한다.

개념 흐름:

```text
source decode
  -> decoded ordinal + PTS
  -> manual annotation lookup
  -> identity 확인
  -> blur 적용
  -> encode
```

### 10.1 기본 매칭

우선 ordinal로 lookup한다.

### 10.2 검증

저장된 annotation에 PTS identity가 있으면 export frame의 PTS와 검증한다.

불일치 시 조용히 다른 프레임에 블러하지 않는다.

정책 후보:

- PTS/occurrence가 일치하면 적용
- 불일치 시 exact timeline을 통해 재확인
- 여전히 identity가 불명확하면 해당 annotation을 오류/검토 대상으로 보고한다

구현 시 "가장 가까운 시간"으로 임의 보정하는 방식은 피한다.

### 10.3 VFR

export에서도 `frame = seconds * fps` 방식은 사용하지 않는다.

decoded ordinal 및 decoded presentation timestamp를 기준으로 한다.

---

## 11. 자동 블러와의 경계

### 11.1 순수 수동

새 플레이어형 구조 사용.

```text
sequential playback
+ pause frame identity
+ manual annotation
```

### 11.2 자동 블러

현재 자동 분석 pipeline 유지.

자동 분석은 frame ordinal별 detector 결과, tracking/post-process, export gate 등의 현재 구조가 이미 있으므로 이번 변경에서 플레이어형 수동 구조로 대체하지 않는다.

### 11.3 자동 블러 후 수동 보정

기존 자동 결과의 frame/PTS mapping을 유지한다.

자동 결과를 수동 화면에서 편집할 때:

- 기존 annotation/mask를 해당 exact frame에 표시
- frame identity 검증을 공유
- 필요한 random exact access 및 caching 개선은 별도 최적화

즉 순수 수동 모드의 "재생 자체"를 자동 결과 보정 흐름에 강제로 적용하지 않는다.

---

## 12. Decoder lifecycle

### 12.1 Manual player decoder

순수 수동 workspace가 소유한다.

역할:

- sequential playback
- current frame identity
- bounded local seek
- forward prefetch

### 12.2 Background thumbnail work

manual player의 현재 frame delivery를 막지 않아야 한다.

구현 방법은 다음 중 하나를 비교 검토한다.

1. 별도 저우선순위 thumbnail extractor
2. 같은 extractor를 사용하되 명시적 priority scheduler 적용

현재 코드에서 하나의 extractor lock에 여러 작업을 동시에 얹는 방식은 사용자 입력 우선순위를 보장하기 어렵기 때문에 그대로 확장하지 않는다.

### 12.3 불필요한 decoder 증식 방지

별도 extractor를 사용한다면:

- manual workspace lifetime에 맞춰 생성/Dispose
- background task 취소 완료 후 Dispose
- 임의 프레임 클릭마다 새 extractor를 만들지 않음
- playback stop/start마다 중첩 decoder가 남지 않도록 직렬화

---

## 13. Loading UI

로딩 UI는 실패 보완책이 아니라 정상적인 long seek 상태 표시다.

### 13.1 표시 조건

- far seek
- uncached previous-frame jump
- decoder 재초기화
- exact identity 확인이 필요한 상황

### 13.2 표시하지 않아야 하는 일반 경로

- sequential playback
- Pause
- ring-buffer hit
- cached neighboring frame

### 13.3 동작

```text
사용자 입력
  -> 1 UI tick 안에 선택 표시
  -> 필요 시 spinner
  -> 이전 화면 유지
  -> 새 frame 도착
  -> atomic replace
  -> spinner 제거
```

사용자가 입력을 했는데 아무 시각 피드백도 없는 상태를 허용하지 않는다.

---

## 14. 취소와 최신 요청 우선

수동 편집의 seek는 latest-request-wins 정책을 사용한다.

예:

```text
seek A 시작
seek B 입력 -> A cancel
seek C 입력 -> B cancel
C만 화면 적용
```

중요 규칙:

- 취소된 요청 결과가 늦게 도착해도 화면에 적용하지 않는다.
- current request id / generation stamp를 검증한다.
- native I/O가 즉시 취소되지 않는 경우에도 완료 결과를 폐기한다.
- 새 사용자 요청이 background queue 뒤에 대기하지 않게 한다.

---

## 15. 기존 코드에서 변경 예상 영역

구현 시 검토 대상:

### `ViewModels/Workspace/FramePreviewViewModel.cs`

- 순수 수동 모드에서 `OnFrameIndexChanged()` 중심 random-access 흐름을 manual-player state machine으로 분리
- current decoded bitmap/identity 소유
- loading 상태 노출
- Pause/Resume 시 bitmap 재요청 제거

### `Services/Video/Session/VideoSession.cs`

- manual mode 전용 player component 도입 검토
- exact/thumbnail shared-extractor 경쟁 구조 분리 검토

### `Services/Video/Session/TimelineController.cs`

- 순수 수동 mode에서는 thumbnail-first await 제거
- far seek command와 normal sequential frame advance 분리

### `Services/Video/FfFrameExtractor.cs`

- sequential decode 시 frame ordinal + decoded PTS + timestamp occurrence를 함께 반환할 수 있는 API
- seek landing frame identity 반환
- 기존 decoded PTS timeline과 identity 통합

### `Controls/TimelineFrameStrip.cs`

- click 즉시 pending-selection 위치 표시
- exact frame 확정 전 loading/pending state 지원
- current decoded PTS 기준 playhead 표시

### workspace/state 저장

- manual annotation에 frame identity 정보 저장
- 기존 저장 형식과 migration 방법 필요

---

## 16. 구현 단계

### Phase 1 - Manual frame identity

- sequential decode 결과에 ordinal/PTS/occurrence identity 제공
- current manual frame identity 보관
- annotation에 identity 저장 가능하도록 모델 확장
- VFR identity unit/integration test 추가

### Phase 2 - Player-style manual workspace

- 순수 수동 모드에서 sequential player 도입
- Pause 시 current bitmap 그대로 유지
- Resume 시 sequential continuation
- one-frame forward 동작 구현

### Phase 3 - Ring buffer

- byte-budget 기반 recent/full-resolution frame cache
- forward/back one-frame 이동 최적화
- memory pressure 테스트

### Phase 4 - Far seek UX

- pending playhead
- loading overlay
- latest-request-wins cancellation
- seek landing identity 확정

### Phase 5 - Background prefetch

- 사용자 요청보다 낮은 priority scheduler
- current+forward prefetch
- viewport thumbnail prefetch
- user seek 시 prefetch scope 재설정

### Phase 6 - Export identity validation

- ordinal + PTS/occurrence 검증
- mismatch fail-safe/reporting
- VFR export integration test

### Phase 7 - 자동 결과 수동 보정 공유

- 기존 Auto mask에 frame identity 검증 적용 가능한 범위 검토
- 순수 수동 player architecture와 Auto 분석 pipeline의 불필요한 결합 방지

---

## 17. 검증 시나리오

구현 완료 판정에는 최소 다음 실제 실행 테스트가 필요하다.

### 일반 CFR

- 30fps 짧은 영상 재생/Pause
- Pause frame과 export blur frame 일치
- 좌우 한 프레임 반복 이동
- 같은 위치 반복 접근 시 즉시 표시

### VFR

- presentation interval이 불규칙한 영상
- Pause identity의 ordinal/PTS 확인
- `time * fps` 없이 export 동일 프레임 적용 확인
- 동일 PTS occurrence가 있는 샘플이 확보되면 구분 검증

### 긴 영상

- 초반에서 후반으로 큰 seek
- 클릭 즉시 pending 표시
- spinner 표시
- background prefetch보다 사용자 seek 우선
- 후반 seek 완료 후 해당 위치부터 prefetch 재시작

### 반복 seek

```text
10s -> 300s -> 20s -> 450s -> 30s
```

- 마지막 요청만 화면 반영
- 이전 결과가 뒤늦게 화면을 덮지 않음
- decoder/task 누수 없음

### 메모리

- 1080p
- 4K
- 장시간 pause/step 반복

확인 항목:

- full-resolution cache byte budget 준수
- bitmap Dispose 누락 없음
- background cache 무제한 증가 없음

### Auto -> manual correction

- 자동 분석 완료 후 특정 프레임 수동 수정
- 기존 mask/frame identity 유지
- export 시 자동 결과 + 수동 수정이 정확한 프레임에 적용

---

## 18. 완료 기준

순수 수동 모드의 목표 UX:

- 재생은 일반 동영상 플레이어처럼 연속적으로 동작한다.
- Pause 후 편집 화면이 별도 exact seek 없이 즉시 유지된다.
- cached 인접 프레임 이동은 즉시 반응한다.
- 먼 위치 jump에서만 로딩 UI가 표시된다.
- 사용자의 직접 요청이 background caching보다 항상 우선한다.
- VFR에서도 사용자가 본 프레임과 export에서 수정되는 프레임이 일치한다.
- `time * fps` 기반 프레임 추정에 의존하지 않는다.
- 전체 영상을 full-resolution bitmap으로 사전 캐싱하지 않는다.
- 자동 블러 pipeline의 현행 분석 구조를 불필요하게 변경하지 않는다.

---

## 19. 비목표

이번 설계의 비목표:

- 모든 영상을 RAM에 전체 디코딩
- 모든 full-resolution frame 영구 캐싱
- OS native media player로 preview decoder 교체
- VFR을 CFR처럼 강제로 재해석
- 자동 분석 pipeline 전체 재작성
- annotation identity 불일치를 nearest timestamp로 조용히 보정

---

## 20. 결론

순수 수동 블러는 random exact-frame viewer보다 **FFmpeg 기반 동영상 플레이어에 annotation editor를 얹는 구조**가 사용자 작업 방식에 더 적합하다.

핵심은 영상 재생 자체보다 **프레임 identity 보존**이다.

```text
사용자가 실제로 본 decoded frame
        =
저장된 manual annotation frame
        =
export 시 blur가 적용되는 decoded frame
```

이 등식이 유지되도록 ordinal + presentation timestamp + timestamp occurrence를 함께 사용하고, VFR에서는 시간과 평균 FPS로 프레임 번호를 추정하지 않는다.

자동 블러 및 자동 결과 수동 보정은 현재 exact/PTS 기반 구조를 유지하면서 필요한 identity 검증과 caching 개선만 공유한다.
