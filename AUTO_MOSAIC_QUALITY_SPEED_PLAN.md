# ?먮룞 寃異?紐⑥옄?댄겕 ?덉쭏/?띾룄 媛쒖꽑 ?ㅺ퀎 湲고쉷

## 紐⑹쟻
?꾩옱 ?먮룞 寃異???紐⑥옄?댄겕 泥섎━???듭떖 臾몄젣????媛吏??

- 寃곌낵 ?덉쭏: ?쇨뎬 誘명깘, ?ㅽ깘, 諛뺤뒪 ?? ?꾨젅??媛?紐⑥옄?댄겕 ?붾뱾由쇱씠 寃곌낵臾??덉쭏???⑥뼱?⑤┛??
- 泥섎━ ?쒓컙: ?먮룞 寃異? ?꾨젅??蹂?? 留덉뒪??釉붾윭, export/encode ?꾩껜 ?쒓컙??湲몃떎.

??臾몄꽌??紐⑤뜽 援먯껜源뚯? ?ы븿???덉쭏???좎??섍굅???믪씠硫댁꽌 ?쒓컙??以꾩씠湲??꾪븳 援ы쁽 怨꾪쉷?대떎. ?⑥닚??寃異?媛꾧꺽???섎━嫄곕굹 ???쏀븳 紐⑤뜽濡?諛붽씀??諛⑹떇? ?덉쭏 ???媛?μ꽦???щ?濡?湲곕낯 ?꾨왂?먯꽌 ?쒖쇅?쒕떎.

## ?꾩옱 ?뺤씤???꾨줈?앺듃 援ъ“
FaceShield??.NET 8 Avalonia ?곗뒪?ы넲 ?깆씠硫??붾（?섏? ?⑥씪 ?꾨줈?앺듃 `FaceShield.csproj`濡?援ъ꽦?섏뼱 ?덈떎. ?꾩옱 ?듭떖 吏꾩엯?먯? ?ㅼ쓬怨?媛숇떎.

- `ViewModels/Pages/HomePageViewModel.cs`: ???먮룞 ?ㅽ뻾, ?먮룞 ?꾨즺 ????? ?덉긽 ?쒓컙/?곹깭 ?쒖떆.
- `ViewModels/Pages/WorkspaceViewModel.cs`: ?먮룞 寃異??ㅽ뻾, ?먮룞 ?쒕떇, ?꾩쿂由? export ?곌껐.
- `Services/Analysis/AutoMaskGenerator.cs`: ?먮룞 寃異??뚯씠?꾨씪?? sparse pipeline, tracking, ROI, 吏꾪뻾 濡쒓렇.
- `Services/Analysis/AutoMaskOptions.cs`: downscale, tracking, 寃異?媛꾧꺽, 蹂묐젹 detector ???듭뀡.
- `Services/FaceDetection/IBgraFaceDetector.cs`: BGRA 湲곕컲 寃異쒓린 援먯껜???명꽣?섏씠??
- `Services/FaceDetection/FaceDetectorFactory.cs`: 寃異쒓린 ?앹꽦 ?⑺넗由?
- `Services/FaceDetection/FaceDetectorBackend.cs`: `FaceOnnx`, `ScrfdOnnx`, `YuNetOnnx`, `YoloFaceOnnx` backend enum.
- `Services/FaceDetection/FaceOnnxDetector.cs`: ?꾩옱 湲곕낯 ?쇨뎬 寃異?援ы쁽.
- `Services/FaceDetection/DetectorAutoTuner.cs`: ?먮룞 ?ㅽ뻾 ?쒖옉 ??ONNX ?ㅽ뻾 ?듭뀡/?몄뀡 ??痢≪젙.
- `Services/Video/VideoExportService.cs`: export, ?됱긽 蹂?? direct face rect blur, bitmap mask blur, encode.
- `Services/Video/MaskedVideoExporter.cs`: ?쇨뎬 ?곸뿭 吏곸젒 釉붾윭 諛?留덉뒪??釉붾윭 ?곸슜.
- `Services/Video/Session/ExactFrameProvider.cs`: ?꾨젅???뺥솗 議고쉶? preview ?덉젙??

?꾩옱 援ъ“??紐⑤뜽 援먯껜??`IBgraFaceDetector`, `FaceDetectorFactory`, `FaceDetectorBackend`, `FaceDetectorFactoryOptions`瑜??뺤옣?섎뒗 諛⑹떇??留욌떎. `AutoMaskGenerator`??`WorkspaceViewModel`????紐⑤뜽??吏곸젒 諛뺤쑝硫??댄썑 鍮꾧탳? 濡ㅻ갚???대젮?뚯쭊??

## ?꾩옱 ?곹깭 ?붿빟
?대? ?ㅼ뼱媛?湲곕컲 ?묒뾽:

- `FaceOnnxDetector`媛 `IBgraFaceDetector`瑜?援ы쁽?쒕떎.
- `AutoMaskGenerator`媛 BGRA/raw 湲곕컲 ?뚯씠?꾨씪?멸낵 sparse 蹂묐젹 ?뚯씠?꾨씪?몄쓣 吏?먰븳??
- `WorkspaceViewModel.RunAutoCoreAsync()`?먯꽌 `DetectorAutoTuner`瑜?`Task.Run`?쇰줈 ?ㅽ뻾??UI thread ?뺤?瑜?以꾩씤??
- `VideoExportService`???먮룞 face rect媛 ?덈뒗 ?꾨젅?꾩뿉???꾩껜 bitmap mask ???`ApplyFaceRectsAndBlur()` direct blur 寃쎈줈瑜??????덈떎.
- export 濡쒓렇??`bitmapMaskFrames`, `directFaceFrames`, `swsToBgraMs`, `maskMs`, `swsToEncMs`, `encodeMs`, `totalMs`媛 ?⑤뒗??
- ???먮룞 吏꾪뻾 ?곹깭??export ?④퀎?먯꽌???④린吏 ?딅뒗 諛⑺뼢?쇰줈 ?섏젙?섏뼱 ?덈떎.

?꾩옱 異붽? ?뺤씤???곹깭:

- `FaceDetectorBackend.YoloFaceOnnx`, `YoloFaceOnnxDetectorOptions`, `YoloFaceModelType`, `YoloFaceOnnxDetector`媛 異붽??섏뼱 YOLOv8-Face/YOLO5Face ONNX ?꾨낫瑜??ㅽ뻾?????덈떎.
- ???먮룞 ?듭뀡?먯꽌 FaceONNX? YOLO Face ONNX瑜??좏깮?????덇퀬, YOLO??紐⑤뜽 醫낅쪟, 紐⑤뜽 寃쎈줈, objectness/confidence/NMS, ?낅젰 ?ш린, tiling 媛믪쓣 FaceONNX threshold? 遺꾨━?댁꽌 媛吏꾨떎. YOLOv8-Face? YOLO5Face ?ъ씠?먯꽌??紐⑤뜽 寃쎈줈? threshold/input/tiling profile??蹂꾨룄濡????蹂듭썝?쒕떎.
- `AutoMaskOptions.FilterProfile`怨?track ?꾩쿂由?profile? FaceONNX/SCRFD/YOLO蹂꾨줈 遺꾨━?섏뼱 ?덉쑝硫? FaceONNX auto-tune? FaceONNX backend?먯꽌留??곸슜?쒕떎.
- YOLO5Face `0.12/0.18/0.45` profile? ???6遺?3珥?gate瑜??듦낵?덉?留?9遺?2珥?諛?6遺?30珥??뺤옣 gate?먯꽌 異붿쿇 ?꾨낫濡??밴꺽?섏? 紐삵뻽??

?⑥? ?쒓퀎:

- ?ㅼ젣 10遺?臾몄젣 ?곸긽 湲곗??쇰줈 ?먮룞 寃異??쒓컙怨?export ?쒓컙??遺꾨━ 痢≪젙?섏뼱 ?덉? ?딅떎.
- YOLO ?뺤옣 gate??mismatch?먮뒗 FaceONNX false-positive, YOLO false-positive, ?ㅼ젣 ?묒? ?쇨뎬 recall, box definition 李⑥씠媛 ?욎뿬 ?덉뼱 ?뺣떟 ?쇰꺼 湲곕컲 ?됯????꾩쭅 ?녿떎.
- YOLO ?꾨낫蹂?profile? ?꾩쭅 理쒖쥌 異붿쿇 ?곹깭媛 ?꾨땲硫? ?泥?YOLO face 紐⑤뜽 ?먮뒗 verifier/refiner ?꾨왂?????꾩슂?섎떎.
- 紐⑤뜽 ?꾨낫蹂?諛고룷 ?ш린, native/provider ?덉젙?? Windows/macOS 吏???щ????꾩쭅 ?뺤젙?섏? ?딆븯??

## ?깅뒫/?덉쭏 紐⑺몴
?뺣웾 紐⑺몴???ㅼ젣 ?섑뵆 痢≪젙 ???뺤젙?쒕떎. 1李?紐⑺몴???ㅼ쓬 湲곗??쇰줈 ?〓뒗??

- ?먮룞 寃異? 10遺??곸긽?먯꽌 寃異??④퀎媛 export蹂대떎 怨쇰룄?섍쾶 湲몃㈃ detector/backend 援먯껜? pipeline 媛쒖꽑???곗꽑?쒕떎.
- export: `maskMs`, `swsToBgraMs`, `swsToEncMs`, `encodeMs` 以?媛??????ぉ遺??以꾩씤??
- ?덉쭏: ?쇨뎬 誘명깘???섏뼱?섎뒗 ?듭뀡? 湲곕낯媛믪쑝濡??곗? ?딅뒗??
- ?덉젙?? DirectML/CoreML/provider ?먮룞 ?뺤옣? 寃利앸맂 寃쎈줈留?湲곕낯媛믪쑝濡??붾떎.
- UX: ?덉긽 ?쒓컙, 吏꾪뻾 ?곹깭, 痍⑥냼 媛?μ꽦? ?깅뒫 媛쒖꽑 怨쇱젙?먯꽌???좎??쒕떎.

## 痢≪젙 癒쇱? ????ぉ
?ㅼ젣 臾몄젣 ?곸긽?쇰줈 ?ㅼ쓬 濡쒓렇瑜?諛섎뱶???뺣낫?쒕떎.

```text
[AutoTune] ...
[AutoMask] frames=..., readMs=..., detectMs=..., maskMs=..., totalMs=...
[AutoMaskPipe] frames=..., decodeMs=..., detectMs=..., totalMs=...
[AutoMaskSparsePipe] detects=..., decoded=..., decodeMs=..., detectMs=..., totalMs=...
[OnnxPerf] calls=..., preMs=..., inferMs=..., totalMs=...
[Export] done frames=..., bitmapMaskFrames=..., directFaceFrames=..., swsToBgraMs=..., maskMs=..., swsToEncMs=..., encodeMs=..., totalMs=...
```

湲곕줉 ?щ㎎:

| ?섑뵆 | ?댁긽??FPS | 湲몄씠 | ?듭뀡 | ?먮룞 寃異?total | detectMs | export total | maskMs | encodeMs | ?덉쭏 硫붾え |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 臾몄젣 ?곸긽 A | ?뺤씤 ?꾩슂 | ?뺤씤 ?꾩슂 | ?꾩옱 湲곕낯媛?| ?뺤씤 ?꾩슂 | ?뺤씤 ?꾩슂 | ?뺤씤 ?꾩슂 | ?뺤씤 ?꾩슂 | ?뺤씤 ?꾩슂 | 誘명깘/?ㅽ깘 援ш컙 湲곕줉 |

痢≪젙 ?꾧퉴吏 "?뺤떎??鍮⑤씪議뚮떎" ?먮뒗 "紐⑤뜽 援먯껜留??섎㈃ ?닿껐?쒕떎"??寃곕줎? ?대━吏 ?딅뒗??

## 紐⑤뜽 援먯껜 ?꾨왂
紐⑤뜽 援먯껜????踰덉뿉 湲곕낯 紐⑤뜽??媛덉븘?롮? ?딄퀬, backend瑜?異붽??????숈씪 ?섑뵆?먯꽌 鍮꾧탳?쒕떎.

### ?꾨낫援?1. ?꾩옱 `FaceONNX`
   - 湲곗??좎쑝濡??좎??쒕떎.
   - 湲곗〈 ?덉쭏/諛고룷 ?덉젙?깆쓣 鍮꾧탳 湲곗??쇰줈 ?쇰뒗??

2. SCRFD 怨꾩뿴 ONNX
   - ?묒? ?쇨뎬怨??ㅼ뼇??媛곷룄?먯꽌 ?덉쭏??醫뗭? ?꾨낫濡?寃?좏븳??
   - ONNX Runtime CPU/DirectML/CoreML provider?먯꽌 ?낅젰/?꾩쿂由?援ы쁽???꾩슂?섎떎.
   - NMS, anchor/grid decode, threshold ?쒕떇??吏곸젒 援ы쁽?댁빞 ?????덈떎.

3. RetinaFace 怨꾩뿴 ONNX
   - ?뺥솗???꾨낫濡?寃?좏븳??
   - ?띾룄媛 ?먮┫ ???덉쑝誘濡?1李??꾩껜 寃異쒕낫?ㅻ뒗 ?섏떖 援ш컙 ?ш?異쒖슜 2李?紐⑤뜽 ?꾨낫濡??붾떎.

4. BlazeFace/MediaPipe 怨꾩뿴
   - 鍮좊Ⅸ 1李?寃異??꾨낫濡?寃?좏븳??
   - ?곸긽 ???묒? ?쇨뎬, 痢〓㈃ ?쇨뎬, 留덉뒪??媛由??곹솴?먯꽌 ?덉쭏 寃利앹씠 ?꾩슂?섎떎.

5. YOLO face 怨꾩뿴 ONNX
   - 諛곗튂 泥섎━, GPU/provider ?쒖슜 媛?μ꽦??寃?좏븳??
   - ?쇨뎬 諛뺤뒪留??꾩슂???꾩옱 援ъ“? ??留욎?留? 紐⑤뜽 ?뚯씪 異쒖쿂? ?쇱씠?좎뒪 ?뺤씤???꾩슂?섎떎.

### 沅뚯옣 援ъ“
?⑥씪 紐⑤뜽 援먯껜蹂대떎 2?④퀎 援ъ“瑜??곗꽑 寃?좏븳??

1. 鍮좊Ⅸ 1李?detector
   - ?꾩껜 ?꾨젅???먮뒗 sparse frame?먯꽌 鍮좊Ⅴ寃??쇨뎬 ?꾨낫瑜?李얜뒗??
   - ??? confidence ?꾨낫???쇰떒 track ?꾨낫濡??④릿??

2. 媛뺥븳 2李?verifier/refiner
   - 誘명깘 媛?μ꽦???믪? 援ш컙, 媛묒옄湲??쇨뎬???щ씪吏?援ш컙, 諛뺤뒪媛 ?ш쾶 ??援ш컙留??ш?異쒗븳??
   - 紐⑤뱺 ?꾨젅?꾩뿉 媛뺥븳 紐⑤뜽???뚮━吏 ?딅뒗??

3. track 湲곕컲 蹂댁젙
   - ?쇨뎬蹂?track id瑜??좎??쒕떎.
   - 吏㏃? 誘명깘 援ш컙? ?댁쟾/?ㅼ쓬 track?쇰줈 蹂닿컙?쒕떎.
   - scene cut ?먮뒗 湲됯꺽???吏곸엫?먯꽌??蹂닿컙???딅뒗??

## 援ы쁽 ?④퀎
### 1?④퀎: 痢≪젙/吏꾨떒 紐⑤뱶 媛뺥솕
紐⑺몴??蹂묐ぉ??寃異? ?붿퐫?? 蹂?? 釉붾윭, ?몄퐫?⑹쑝濡?遺꾨━?섎뒗 寃껋씠??

- `AutoMaskGenerator`??run summary 媛앹껜瑜?異붽??쒕떎.
- `WorkspaceViewModel`?먯꽌 ?먮룞 寃異??꾨즺 ??summary瑜?濡쒓렇? UI ?곹깭???④릿??
- export summary? auto summary瑜?媛숈? run id濡?臾띕뒗??
- ???먮룞 ???寃쎈줈?먯꽌???숈씪??run id瑜??④릿??
- 誘명깘/?ㅽ깘 ?뺤씤?⑹쑝濡?"?댁긽 ?꾨낫 ?꾨젅?? 紐⑸줉????ν븯嫄곕굹 export ??workspace?먯꽌 ?뺤씤 媛?ν븯寃??쒕떎.

?곗텧臾?

- `Services/Analysis/AutoMaskRunSummary.cs`
- `Services/Video/ExportRunSummary.cs`
- 濡쒓렇 ?? `[AutoRunSummary] runId=..., autoMs=..., exportMs=..., detector=..., options=...`

### 2?④퀎: detector backend ?뺤옣 吏???뺣━
紐⑺몴????紐⑤뜽???덉쟾?섍쾶 遺숈씠怨??숈씪 ?듭뀡?쇰줈 鍮꾧탳?섎뒗 寃껋씠??

- `FaceDetectorBackend`?????꾨낫 enum??異붽??쒕떎.
- `FaceDetectorFactoryOptions`??backend蹂?options瑜?遺꾨━?쒕떎.
- 紐⑤뱺 detector??`IBgraFaceDetector`瑜?援ы쁽?쒕떎.
- detector蹂??낅젰 ?ш린, threshold, NMS, provider ?듭뀡??紐낆떆?쒕떎.
- backend ?좏깮? ?꾩떆濡?媛쒕컻???ㅼ젙 ?먮뒗 ?대? ?듭뀡?쇰줈留??닿퀬, 寃利????ъ슜??湲곕낯媛믪쑝濡??몄텧?섏? ?딅뒗??

?덉긽 蹂寃??뚯씪:

- `Services/FaceDetection/FaceDetectorBackend.cs`
- `Services/FaceDetection/FaceDetectorFactory.cs`
- `Services/FaceDetection/FaceDetectorFactoryOptions.cs`
- `Services/FaceDetection/*DetectorOptions.cs`
- `Services/FaceDetection/*Detector.cs`

### 3?④퀎: 紐⑤뜽 ?꾨낫 A/B 踰ㅼ튂
紐⑺몴??媛숈? ?곸긽, 媛숈? ?듭뀡, 媛숈? export 寃쎈줈?먯꽌 ?꾨낫瑜?鍮꾧탳?섎뒗 寃껋씠??

鍮꾧탳 ??ぉ:

- 寃異?total time
- frame??detector latency
- 誘명깘 援ш컙 ??- ?ㅽ깘 援ш컙 ??- ?쇨뎬 諛뺤뒪 jitter
- ?묒? ?쇨뎬 寃異쒕쪧
- DirectML/CoreML/CPU fallback ?덉젙??- 諛고룷 ?뚯씪 ?ш린? native ?섏〈??
?먯젙:

- 鍮좊Ⅴ吏留?誘명깘???섎㈃ 湲곕낯 detector濡??곗? ?딅뒗??
- ?뺥솗?섏?留??먮━硫?2李?verifier/refiner濡??쒗븳?쒕떎.
- provider 珥덇린???ㅽ뙣???λ퉬蹂??몄감媛 ?щ㈃ 湲곕낯媛믪뿉???쒖쇅?쒕떎.

### 4?④퀎: track-first ?먮룞 紐⑥옄?댄겕
紐⑺몴??`DetectEveryNFrames > 1`?먯꽌???쇨뎬???딄린嫄곕굹 ?吏 ?딄쾶 留뚮뱶??寃껋씠??

- `FaceTrack` 紐⑤뜽??異붽??쒕떎.
- 寃異?寃곌낵瑜?frame ?⑥쐞 dictionary留뚯쑝濡?蹂댁? ?딄퀬 track ?⑥쐞濡?愿由ы븳??
- IoU, 以묒떖??嫄곕━, ?ш린 蹂?붿쑉濡?媛숈? ?쇨뎬 ?щ?瑜??먮떒?쒕떎.
- 吏㏃? 誘명깘 援ш컙? ?댁쟾/?ㅼ쓬 寃異?諛뺤뒪濡?蹂닿컙?쒕떎.
- 媛묒옉?ㅻ윭??scene cut, ?붾㈃ ?꾪솚, ???꾩튂 蹂?붿뿉?쒕뒗 track???딅뒗??
- ?섎룞 ?몄쭛 ?꾨젅?꾩? ?먮룞 smoothing/track 蹂댁젙 ??곸뿉???쒖쇅?쒕떎.

?덉긽 異붽? ?뚯씪:

- `Services/Analysis/FaceTrack.cs`
- `Services/Analysis/FaceTrackBuilder.cs`
- `Services/Analysis/FaceTrackInterpolator.cs`

### 5?④퀎: ROI ?ш?異쒓낵 ?덉쭏 寃뚯씠??紐⑺몴???꾩껜 ?꾨젅???ш?異??놁씠 誘명깘 媛?μ꽦???믪? 援ш컙留?蹂닿컯?섎뒗 寃껋씠??

- ?쇨뎬??媛묒옄湲??щ씪吏?援ш컙??`suspicious gap`?쇰줈 ?쒖떆?쒕떎.
- ?댁쟾 track 二쇰? ROI瑜??뺣??댁꽌 ?ш?異쒗븳??
- confidence媛 ??굅??諛뺤뒪媛 ???꾨젅?꾨쭔 媛뺥븳 detector濡??ш?異쒗븳??
- ?ш?異?寃곌낵媛 湲곗〈 track怨?異⑸룎?섎㈃ ???덉젙?곸씤 履쎌쓣 ?좏깮?쒕떎.
- 蹂댁젙 ????face rect瑜?濡쒓렇濡?鍮꾧탳?쒕떎.

?덉쭏 寃뚯씠??

- 媛숈? ?쇨뎬 track??3~5?꾨젅???댄븯濡??щ씪議뚮떎媛 ?뚯븘?ㅻ㈃ 蹂닿컙 ?먮뒗 ROI ?ш?異쒖쓣 ?쒕룄?쒕떎.
- 諛뺤뒪 硫댁쟻??吏곸쟾 ?鍮?鍮꾩젙?곸쟻?쇰줈 而ㅼ?嫄곕굹 ?묒븘吏硫??꾨낫濡쒕쭔 ?먭퀬 利됱떆 諛섏쁺?섏? ?딅뒗??
- ?쇨뎬 ?섍? 媛묒옄湲??ш쾶 蹂?섎㈃ scene cut ?щ?瑜?癒쇱? ?먮떒?쒕떎.

### 6?④퀎: export 寃쎈줈 異붽? 理쒖쟻??紐⑺몴???먮룞 face rect 寃쎈줈瑜?理쒕???bitmap mask 寃쎈줈濡??⑥뼱?⑤━吏 ?딅뒗 寃껋씠??

- ?먮룞 face rect留??덈뒗 ?꾨젅?꾩? 怨꾩냽 `ApplyFaceRectsAndBlur()` 寃쎈줈瑜??ъ슜?쒕떎.
- ?섎룞 mask媛 ?덈뒗 ?꾨젅?꾨쭔 `ApplyMaskAndBlur()`瑜??ъ슜?쒕떎.
- face rect媛 ?녿뒗 ?꾨젅?꾩? BGRA 蹂???놁씠 decode frame??諛붾줈 encode?쒕떎.
- `swsToBgraMs`? `swsToEncMs`媛 ?щ㈃ 蹂???ъ궗???먮뒗 encoder pixel format 議곗젙??寃?좏븳??
- `encodeMs`媛 蹂묐ぉ?대㈃ preset/profile/CRF ?먮뒗 ?섎뱶?⑥뼱 ?몄퐫?⑹쓣 蹂꾨룄 ?ㅽ뿕?쒕떎.

二쇱쓽:

- ?붿쭏 ?먯떎???쇱쑝?ㅻ뒗 ?몄퐫???듭뀡 蹂寃쎌? 湲곕낯媛믪쑝濡?諛붾줈 ?ｌ? ?딅뒗??
- ?ㅻ뵒??sync? frame count exactness???좎??댁빞 ?쒕떎.

### 7?④퀎: UX ?좎?
?깅뒫 媛쒖꽑 以묒뿉???ъ슜?먭? ?먮겮???곹깭 ?쒖떆???좎??쒕떎.

- ?먮룞 寃異?以??덉긽 ?쒓컙 ?쒖떆 ?좎?.
- export 以??덉긽 ?쒓컙 ?먮뒗 ?④퀎 ?쒖떆 ?좎?.
- ?먮룞 ?쒕떇 以?UI thread block 湲덉?.
- 痍⑥냼 ???뺤긽 痍⑥냼濡?泥섎━?섍퀬 ?ㅻ쪟泥섎읆 蹂댁씠吏 ?딄쾶 ?쒕떎.
- ???먮룞 ??κ낵 ?뚰겕?ㅽ럹?댁뒪 ?먮룞 ?ㅽ뻾???곹깭 ?쒖떆瑜?蹂꾨룄濡??뺤씤?쒕떎.

## 湲곕낯媛??뺤콉
寃利???

- 湲곕낯 detector???꾩옱 ?덉젙?곸씤 `FaceOnnx` ?좎?.
- ??backend???대? ?듭뀡 ?먮뒗 媛쒕컻 ?ㅼ젙?쇰줈留??좏깮.
- GPU/provider??寃利??꾧퉴吏 湲곕낯媛믪쑝濡??밴꺽?섏? ?딅뒗??
- 寃異?媛꾧꺽 利앷???threshold ?꾪솕??湲곕낯 ?덉쭏 媛쒖꽑梨낆쑝濡??곗? ?딅뒗??

寃利???

- ?덉쭏??媛숈???鍮좊Ⅸ detector??湲곕낯 1李?detector ?꾨낫濡??밴꺽?쒕떎.
- ?먮━吏留?誘명깘??以꾩씠??detector??2李?verifier/refiner濡??ъ슜?쒕떎.
- 寃利앸맂 GPU/provider??湲곕낯 ?꾨낫濡??밴꺽?섎릺, ?λ퉬蹂??ㅽ뙣??CPU fallback怨??곹깭 濡쒓렇濡?紐낇솗???④릿??

## 寃利?怨꾪쉷
理쒖냼 寃利?

- `dotnet build FaceShield.sln`
- 吏㏃? ?섑뵆 ?곸긽 open, preview, ?먮룞 寃異? workspace 蹂댁젙, export ?뺤씤.
- 臾몄젣 10遺??곸긽 ?먮룞 ???寃쎈줈 ?ㅽ뻾.
- Windows `win-x64` publish output native DLL ?뺤씤.
- macOS ARM64??紐⑤뜽/native 蹂寃쎌씠 ?덉쑝硫?蹂꾨룄 publish? ?ㅽ뻾 ?뺤씤.

?덉쭏 寃利?

- 誘명깘??諛쒖깮??援ш컙??frame index濡?湲곕줉.
- ?ㅽ깘??諛쒖깮??援ш컙??frame index濡?湲곕줉.
- 諛뺤뒪 ?먯씠 ?덉뿉 ?꾨뒗 援ш컙??frame index濡?湲곕줉.
- 湲곗〈 FaceONNX 寃곌낵? ??backend 寃곌낵瑜?媛숈? frame?먯꽌 鍮꾧탳.

?깅뒫 寃利?

- ?먮룞 寃異?total.
- export total.
- detector latency.
- `maskMs`, `encodeMs`, `swsToBgraMs`, `swsToEncMs`.
- UI 硫덉땄 ?щ?.
- ETA/status ?쒖떆 ?좎? ?щ?.

## ?묒뾽 ?곗꽑?쒖쐞
1. ?ㅼ젣 臾몄젣 ?곸긽 湲곗? baseline 濡쒓렇 ?뺣낫.
2. run summary ???援ъ“ 異붽?.
3. `FaceDetectorBackend` ?뺤옣 援ъ“ ?뺣━.
4. ??detector ?꾨낫 1媛쒕? proof-of-concept濡?異붽?.
5. 媛숈? ?곸긽?먯꽌 FaceONNX? ?꾨낫 detector 鍮꾧탳.
6. track-first 蹂댁젙 異붽?.
7. ROI ?ш?異?2李?verifier 異붽?.
8. export 蹂묐ぉ蹂?理쒖쟻??
9. 湲곕낯媛??밴꺽 ?щ? 寃곗젙.

## 由ъ뒪??- ??紐⑤뜽???쇱씠?좎뒪??諛고룷 媛???щ?媛 遺덈챸?뺥븯硫??쒗뭹 湲곕낯媛믪쑝濡??????녿떎.
- DirectML/CoreML provider???λ퉬蹂꾨줈 珥덇린???ㅽ뙣 ?먮뒗 ?먮┛ fallback???앷만 ???덈떎.
- 寃異??띾룄留?媛쒖꽑?섍퀬 track 蹂댁젙???섏? ?딆쑝硫?寃곌낵臾쇱? ??遺덉븞?뺥빐吏????덈떎.
- ?섎뱶?⑥뼱 ?몄퐫?⑹? ?띾룄??鍮⑤씪吏????덉?留??붿쭏, ?명솚?? 諛고룷 ?섏〈??由ъ뒪?ш? ?덈떎.
- 痢≪젙 ?놁씠 ?щ윭 理쒖쟻?붾? ??踰덉뿉 ?ｌ쑝硫??대뼡 蹂寃쎌씠 ?덉쭏/?띾룄???곹뼢??以щ뒗吏 ?????녿떎.

## 寃곕줎
?대쾲 臾몄젣??"寃異?紐⑤뜽留?媛踰쇱슫 寃껋쑝濡?援먯껜"?섎뒗 ?앹쑝濡?泥섎━?섎㈃ ?덉쭏 ???媛?μ꽦???щ떎. ?꾩옱 ?꾨줈?앺듃?먮뒗 ?대? detector factory, BGRA detector interface, sparse pipeline, direct face rect export, export timing log媛 ?덉쑝誘濡???湲곕컲???대젮???쒕떎.

媛???꾩떎?곸씤 諛⑺뼢? ?ㅼ쓬 ?쒖꽌??

1. ?ㅼ젣 臾몄젣 ?곸긽 湲곗??쇰줈 蹂묐ぉ???섏튂?뷀븳??
2. backend 援먯껜 援ъ“瑜??뺤옣????detector ?꾨낫瑜??덉쟾?섍쾶 遺숈씤??
3. 鍮좊Ⅸ 1李?寃異쒓낵 媛뺥븳 2李??ш?異쒖쓣 遺꾨━?쒕떎.
4. track-first 蹂댁젙?쇰줈 誘명깘怨?諛뺤뒪 ?먯쓣 以꾩씤??
5. export 蹂묐ぉ? 濡쒓렇 ??ぉ蹂꾨줈 以꾩씤??

???쒖꽌濡?媛???덉쭏???ъ깮?섏? ?딄퀬 ?먮룞 寃異???紐⑥옄?댄겕 泥섎━ ?쒓컙??以꾩씪 ???덈떎.

## 2026-05-11 1李?援ы쁽 湲곕줉
?묒뾽 釉뚮옖移? `plan/auto-mosaic-quality-speed`

諛섏쁺 ?댁슜:

- `Services/Analysis/AutoMaskGenerator.cs`??sparse tracking materialize 寃쎈줈瑜??섏젙?덈떎.
- 湲곗〈?먮뒗 寃異??ㅽ봽?덉엫 ?ъ씠 以묎컙 ?꾨젅?꾩뿉 留덉?留??쇨뎬 諛뺤뒪瑜??⑥닚 蹂듭궗?덈떎.
- ?섏젙 ?꾩뿉???ㅼ쓬 寃異??ㅽ봽?덉엫???쇨뎬 諛뺤뒪? IoU, 以묒떖??嫄곕━, 硫댁쟻 蹂?붿쑉濡?媛숈? ?쇨뎬 ?꾨낫瑜?留ㅼ묶?쒕떎.
- 留ㅼ묶 媛?ν븳 寃쎌슦 以묎컙 ?꾨젅?꾩쓽 ?쇨뎬 諛뺤뒪瑜??좏삎 蹂닿컙?쒕떎.
- 吏㏃? 寃異??ㅽ뙣 gap? ?욌뮘 湲띿젙 寃異쒖씠 媛숈? ?쇨뎬濡??먮떒????蹂닿컙?쇰줈 梨꾩슫??
- gap 蹂닿컙 以?留ㅼ묶?섏? ?딆? 諛뺤뒪??蹂듭궗?섏? ?딆븘 ?ㅽ깘/?붿긽 ?좎? ?꾪뿕??以꾩씤??
- 留ㅼ묶?????녿뒗 寃쎌슦?먮뒗 湲곗〈泥섎읆 ?꾩옱 ?ㅽ봽?덉엫 諛뺤뒪瑜??쒗븳??援ш컙?먮쭔 ?ъ슜?쒕떎.
- 留덉?留?寃異?諛뺤뒪瑜??곸긽 ?앷퉴吏 臾댁젣??蹂듭궗?섏? ?딄퀬, ?ㅼ쓬 寃異?援ш컙 ?먮뒗 寃異?媛꾧꺽 ?덉뿉?쒕쭔 materialize?쒕떎.
- `[AutoMaskSparsePipe] done` 濡쒓렇??`interpolated` 媛믪쓣 異붽???sparse tracking?쇰줈 梨꾩썙吏??꾨젅???섎? ?뺤씤?????덇쾶 ?덈떎.

紐⑹쟻:

- `DetectEveryNFrames > 1`怨?tracking 議고빀?먯꽌 寃異??잛닔瑜??섎━吏 ?딄퀬??諛뺤뒪 怨꾨떒 ?꾩긽怨??꾨젅??媛??붾뱾由쇱쓣 以꾩씤??
- ?쇨뎬???щ씪吏???留덉?留?諛뺤뒪媛 怨쇰룄?섍쾶 ?좎??섎뒗 ?덉쭏 由ъ뒪?щ? 以꾩씤??
- 異붽? detector ?몄텧 ?놁씠 以묎컙 ?꾨젅?꾩쓣 蹂댁젙?섎?濡??띾룄 鍮꾩슜? ??쾶 ?좎??쒕떎.

?꾩쭅 ?뺤떎?섏? ?딆? ??

- ?ㅼ젣 臾몄젣 ?곸긽?먯꽌 ?덉쭏 媛쒖꽑 ?뺣룄? 泥섎━ ?쒓컙 蹂?붾뒗 ?꾩쭅 痢≪젙?섏? ?딆븯??
- ?ㅼ쓬 ?④퀎?먯꽌???ㅼ젣 ?섑뵆 濡쒓렇??`detectMs`, `interpolated`, `totalMs`, export timing??鍮꾧탳?댁빞 ?쒕떎.

## 2026-05-11 2李?援ы쁽 湲곕줉
諛섏쁺 ?댁슜:

- `Services/Analysis/AutoMaskRunSummary.cs`瑜?異붽??덈떎.
- `AutoMaskGenerator`媛 ?먮룞 寃異??꾨즺 ??`LastRunSummary`瑜?蹂닿??섍퀬 `[AutoRunSummary]` 濡쒓렇瑜??④린?꾨줉 ?덈떎.
- summary?먮뒗 mode, totalFrames, processed, decoded, detects, interpolated, read/decode/detect/mask/total ms, downscale, tracking, detect interval, parallel detector count, ROI summary媛 ?ы븿?쒕떎.

紐⑹쟻:

- ?ㅼ젣 臾몄젣 ?곸긽 ?ㅽ뻾 ???먮룞 寃異?蹂묐ぉ??export 濡쒓렇? 遺꾨━?댁꽌 ?뺤씤?쒕떎.
- `sequential`, `pipe-single`, `pipe-parallel`, `sparse-pipe-parallel` 寃쎈줈蹂?泥섎━ ?쒓컙??媛숈? ?щ㎎?쇰줈 鍮꾧탳?쒕떎.
- ?ν썑 紐⑤뜽 援먯껜??ROI ?ш?異쒖쓣 ?곸슜?????덉쭏/?띾룄 鍮꾧탳 湲곗??좎쓣 ?뺣낫?쒕떎.

?꾩쭅 ?뺤떎?섏? ?딆? ??

- ?ㅼ젣 臾몄젣 ?곸긽 濡쒓렇媛 ?놁쑝誘濡??대뼡 寃쎈줈媛 理쒖쥌 蹂묐ぉ?몄????꾩쭅 ?????녿떎.

## 2026-05-11 3李?援ы쁽 湲곕줉
諛섏쁺 ?댁슜:

- `Services/FaceDetection/DetectorAutoTuner.cs` ?대? ?꾨낫 痢≪젙 猷⑦봽??`CancellationToken`???곌껐?덈떎.
- `WorkspaceViewModel.RunAutoCoreAsync()`?먯꽌 ?먮룞 ?쒕떇 ?몄텧 ???먮룞 ?ㅽ뻾 痍⑥냼 ?좏겙???꾨떖?섎룄濡??덈떎.
- ?쒕떇 ?쒖옉 ?? ?섑뵆 ?꾨젅??濡쒕뵫, ?꾨낫 痢≪젙 ?? warm-up/痢≪젙 猷⑦봽 ?대??먯꽌 痍⑥냼瑜??뺤씤?쒕떎.

紐⑹쟻:

- ?먮룞 ?ㅽ뻾 珥덈컲 ?쒕떇 以?痍⑥냼?덉쓣 ??UI媛 ?ㅻ옒 硫덉텣 寃껋쿂??蹂댁씠???꾪뿕??以꾩씤??
- ?깅뒫 ?쒕떇???좎??섎㈃?쒕룄 ?ъ슜?먭? 痍⑥냼瑜??꾨Ⅴ硫???鍮좊Ⅴ寃??뺤긽 痍⑥냼 寃쎈줈濡?鍮좎쭊??

?꾩쭅 ?뺤떎?섏? ?딆? ??

- 媛?detector inference ?몄텧 ?먯껜???몃? ?쇱씠釉뚮윭由??몄텧?대?濡??몄텧 以묎컙??媛뺤젣濡?以묐떒???섎뒗 ?녿떎.
- 痍⑥냼 諛섏쓳??媛쒖꽑 ?뺣룄???ㅼ젣 ?곸긽怨??λ퉬?먯꽌 ?뺤씤?댁빞 ?쒕떎.

## 2026-05-11 4李?援ы쁽 湲곕줉
諛섏쁺 ?댁슜:

- 珥덇린 ?ㅽ뿕?먯꽌???좉퇋/珥덇린 ?먮룞 紐⑥옄?댄겕 湲곕낯媛믪쓣 `異붿쟻 ?ъ슜=true`, `2?꾨젅?꾨쭏??寃異?濡?議곗젙?덈떎.
- ?댄썑 ?ㅼ젣 `srcTest` 6遺?援ш컙 smoke?먯꽌 `DetectEveryNFrames=2`媛 baseline-only/optimized-only ?꾨젅??李⑥씠瑜?留뚮뱾 ???덉쓬???뺤씤?덈떎.
- 理쒖쥌 湲곕낯媛믪? ?덉쭏 ?곗꽑 湲곗???留욎떠 `DetectEveryNFrames=1`, `DownscaleRatio=1.0`, `ParallelDetectorCount=2`濡??뺣━?덈떎.
- 湲곗〈 ????ㅼ젙???덉쑝硫???λ맂 ?ъ슜???ㅼ젙???곗꽑 ?곸슜?쒕떎.
- `Services/Workspace/WorkspaceStateStore.cs`???먮룞 ?ㅼ젙 湲곕낯媛믩룄 理쒖쥌 湲곕낯媛믨낵 留욎톬??
- 援щ쾭??????ㅼ젙?먮뒗 `SettingsVersion`???놁쑝誘濡? ?댁쟾 ?ㅽ뿕 湲곕낯媛믪씤 `DetectEveryNFrames=2`??downscale 媛믪씠 湲곕낯泥섎읆 ?⑥? ?딅룄濡?legacy ?ㅼ젙? ?덉쟾 湲곕낯媛믪쑝濡?留덉씠洹몃젅?댁뀡?쒕떎.

紐⑹쟻:

- 湲곕낯 寃쎈줈?먯꽌??紐⑤뱺 ?꾨젅??寃異쒖쓣 ?좎???baseline怨?媛숈? ?덉쭏??紐⑺몴濡??쒕떎.
- ?띾룄 媛쒖꽑? 寃異?媛꾧꺽 利앷????ㅼ슫?ㅼ??쇱씠 ?꾨땲??`pipe-parallel` 蹂묐젹 寃異?寃쎈줈濡?媛?멸컙??

?꾩쭅 ?뺤떎?섏? ?딆? ??

- ?꾩껜 ?곸긽???ㅼ뼇???쇨뎬/?λ㈃ 援ш컙?먯꽌??baseline怨??꾩쟾 ?쇱튂?섎뒗吏??異붽? 寃利앹씠 ?꾩슂?섎떎.

## 2026-05-11 5李?援ы쁽 湲곕줉
諛섏쁺 ?댁슜:

- `UseTracking=true`, `DetectEveryNFrames > 1`?대㈃ 蹂묐젹 ?몄뀡 ?섍? 1濡??쒕떇?섎뜑?쇰룄 sparse pipeline????꾨줉 ?섏젙?덈떎.
- 湲곗〈 議곌굔? `ParallelDetectorCount > 1`???뚮쭔 sparse pipeline???ъ슜?댁꽌, ?먮룞 ?쒕떇 寃곌낵媛 1?몄뀡?대㈃ sequential tracking?쇰줈 ?⑥뼱吏????덉뿀??

紐⑹쟻:

- ?ъ슜?먭? `DetectEveryNFrames > 1` 異붿쟻 ?듭뀡???좏깮?덉쓣 ??sparse materialize 蹂닿컙 濡쒖쭅???덉젙?곸쑝濡??곸슜?쒕떎.
- ?λ퉬???곕씪 1?몄뀡??媛??鍮좊Ⅴ寃??쒕떇?섏뼱???덉쭏 蹂댁젙 寃쎈줈瑜??껋? ?딄쾶 ?쒕떎.

## 2026-05-11 6李?援ы쁽 湲곕줉
諛섏쁺 ?댁슜:

- `Services/Video/ExportRunSummary.cs`瑜?異붽??덈떎.
- `VideoExportService`媛 export ?꾨즺 ??`LastExportSummary`瑜?蹂닿??섍퀬 `[ExportRunSummary]` 濡쒓렇瑜??④린?꾨줉 ?덈떎.
- `WorkspaceViewModel.SaveVideoAsync()`?먯꽌 export summary瑜?workspace 濡쒓렇?먮룄 ?곌껐?덈떎.
- `AutoMaskRunSummary`? `ExportRunSummary`??`runId`瑜?異붽????먮룞 寃異쒓낵 export 濡쒓렇瑜?媛숈? ?ㅽ뻾 ?⑥쐞濡?臾띠쓣 ???덇쾶 ?덈떎.
- `AutoMaskRunSummary`??detector ?대쫫??異붽???紐⑤뜽/backend 援먯껜 ??媛숈? 濡쒓렇 ?щ㎎?쇰줈 湲곗??좉낵 ?꾨낫瑜?鍮꾧탳?????덇쾶 ?덈떎.
- `WorkspaceViewModel.RunAutoCoreAsync()`???먮룞 ?ㅽ뻾留덈떎 `auto-{guid}` ?뺤떇??run id瑜?留뚮뱾怨? ?먮룞 寃異???export源뚯? 媛숈? 媛믪쓣 ?꾨떖?쒕떎.
- ?섎룞 export??`export-{guid}` ?뺤떇??run id瑜??④꺼 ?먮룞 ?ㅽ뻾 濡쒓렇? 援щ텇?쒕떎.
- 釉붾윭 ??곸씠 ?놁뼱 remux-copy濡?議곌린 醫낅즺?섎뒗 export 寃쎈줈??`[ExportRunSummary]`瑜??④린?꾨줉 ?덈떎.

紐⑹쟻:

- ?먮룞 寃異?summary? export summary瑜?媛숈? ?뚯뒪???ㅽ뻾?먯꽌 鍮꾧탳?쒕떎.
- `swsToBgraMs`, `maskMs`, `swsToEncMs`, `encodeMs`, `totalMs`瑜?援ъ“?뷀빐 ?ㅼ쓬 理쒖쟻???곗꽑?쒖쐞瑜??〓뒗??
- ???먮룞 ???寃쎈줈泥섎읆 ?먮룞 寃異?吏곹썑 export媛 ?댁뼱吏??寃쎌슦?먮룄 `[AutoRunSummary]`? `[ExportRunSummary]`瑜?`runId`濡??뺥솗??留ㅼ묶?쒕떎.
- ?쇨뎬??寃異쒕릺吏 ?딅뒗 ?뚯뒪??援ш컙?먯꽌??export ?④퀎媛 ?꾨씫?섏? ?딆븯?붿? ?뺤씤?????덈떎.
- ?ㅼ젣 6遺?援ш컙 smoke?먯꽌 sparse face rect媛 ?덈뒗 ?곹깭濡?hybrid copy export媛 `Invalid argument`瑜?諛쒖깮?쒗궎??寃껋쓣 ?뺤씤?덈떎.
- encoder ?⑦궥 timestamp rescale??蹂닿컯?덉?留?媛숈? 異쒕젰 ?ㅽ듃由쇱뿉???ъ씤肄붾뵫 ?⑦궥怨??먮낯 ?⑦궥???욌뒗 寃쎄퀎 ?ㅽ뙣媛 怨꾩냽?섏뼱, 釉붾윭 ??곸씠 ?덈뒗 寃쎌슦??hybrid copy??鍮꾪솢?깊솕?덈떎.
- 釉붾윭 ??곸씠 ?꾪? ?녿뒗 remux-copy 怨좎냽 寃쎈줈???좎??쒕떎.

## 2026-05-11 srcTest smoke ?쒕룄
???

- `srcTest/260102_jp_10.mp4`
- ?뺤씤??硫뷀??곗씠?? 3840x2160, ??29.97fps, ??1067.6珥? ??31996?꾨젅??
?쒕룄:

- ?꾩껜 ?곸긽? ?ㅻ옒 嫄몃━誘濡?10珥?援ш컙???섎씪 smoke ?ㅽ뻾???쒕룄?덈떎.
- WSL ?꾩떆 harness?먯꽌??FFmpeg native load媛 ?ㅽ뙣?덈떎. ?쒖뒪??`libavcodec.so.60`? 濡쒕뱶?섏?留?`FFmpeg.AutoGen 8.0.0` 諛붿씤?⑷낵 ?고???FFmpeg ABI媛 留욎? ?딆븘 `avcodec_version()` ?④퀎?먯꽌 以묐떒?먮떎.
- Windows `dotnet.exe`濡?repo??Windows native DLL 寃쎈줈瑜??댁슜??smoke???쒕룄?덉쑝?? ?꾩옱 WSL interop?먯꽌 `UtilBindVsockAnyPort` ?ㅻ쪟濡??ㅽ뻾???쒖옉?섏? ?딆븯??

寃곕줎:

- 理쒖큹 WSL ?⑤룆 ?ㅽ뻾?먯꽌??FFmpeg ABI 臾몄젣? Windows interop 臾몄젣濡?smoke瑜??꾨즺?섏? 紐삵뻽??
- ?댄썑 Windows PowerShell harness? WSL ffmpeg ?대┰ ?앹꽦??議고빀??`srcTest` ?ㅼ젣 吏㏃? 援ш컙 smoke瑜??꾨즺?덈떎.
- ?ㅼ젣 ?곸긽 湲곗? `[AutoRunSummary]`, `[ExportRunSummary]`, baseline/optimized face rect 鍮꾧탳 濡쒓렇瑜??뺣낫?덈떎.
- ?꾩쭅 ?꾩껜 17遺??곸긽 湲곗? 理쒖쥌 ?섏튂???뺣낫?섏? ?딆븯??

異붽? ?곗텧臾?

- `scripts/run-srcTest-smoke.ps1`
- `scripts/verify-face-track-postprocess.ps1`
- Windows PowerShell?먯꽌 `.\scripts\run-srcTest-smoke.ps1 -Start 00:02:00 -Seconds 10` ?뺥깭濡??ㅽ뻾?섎㈃ 10珥??대┰??留뚮뱾怨?湲곗???`baseline-all-frames`)怨?媛쒖꽑 寃쎈줈(`optimized-track-2`)???먮룞 寃異?export summary瑜?異쒕젰?쒕떎.
- 湲곗????ㅽ뻾???앸왂?섎젮硫?`-SkipBaseline`??遺숈씤??
- ?꾩젣: Windows `dotnet`怨?`ffmpeg` CLI媛 PATH???덉뼱???쒕떎.
- ?ㅽ겕由쏀듃媛 ?앹꽦?섎뒗 C# harness???꾩떆 ?꾨줈?앺듃濡?`dotnet build` 寃利앹쓣 ?듦낵?덈떎.
- Windows PATH??`ffmpeg`媛 ?놁쑝硫?`-SkipTrim -Source <?대? 留뚮뱺 吏㏃? ?대┰>`?쇰줈 ?ㅽ뻾?????덇쾶 ?덈떎.
- 3珥??대┰ smoke 1??寃곌낵 ?대떦 援ш컙?먯꽌???쇨뎬??0媛?寃異쒕릱?? ??寃곌낵???띾룄 寃쎈줈 ?뺤씤?먮뒗 ?좏슚?섏?留??덉쭏 鍮꾧탳 ?섑뵆濡쒕뒗 遺議깊븯??

?ㅼ젣 6遺?援ш컙 3珥??대┰ smoke:

| 耳?댁뒪 | 寃쎈줈 | 寃異??몄텧 | 蹂닿컙 | faceMaskFrames | ?먮룞 寃異?total | detectMs | export total | directFaceFrames |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline-all-frames | `pipe-single` | 90 | 0 | 8 | 73,731ms | 73,348ms | 7,814ms | 8 |
| optimized-track-2 | `sparse-pipe-parallel` | 45 | 4 | 8 | 26,209ms | 25,857ms | 7,978ms | 8 |
| optimized-track-2-parallel-2 | `sparse-pipe-parallel` | 45 | 4 | 8 | 20,808ms | 40,478ms | 9,369ms | 8 |
| optimized-track-3-parallel-2 | `sparse-pipe-parallel` | 30 | 6 | 9 | 12,759ms | 23,227ms | 8,559ms | 9 |
| optimized-all-1-parallel-2 | `pipe-parallel` | 90 | 0 | 8 | 36,819ms | 72,635ms | 7,061ms | 8 |
| optimized-track-1-parallel-2 | `pipe-parallel` | 90 | 0 | 8 | 33,638ms | 66,588ms | 6,604ms | 8 |
| optimized-track-2-scale-0.75 | `sparse-pipe-parallel` | 45 | 4 | 8 | 19,948ms | 19,614ms | 8,553ms | 8 |
| optimized-track-2-scale-0.5 | `sparse-pipe-parallel` | 45 | 3 | 6 | 14,196ms | 13,933ms | 9,131ms | 6 |

?댁꽍:

- 媛숈? 92?꾨젅???대┰?먯꽌 理쒖쟻??寃쎈줈??寃異??몄텧??90?뚯뿉??45?뚮줈 以꾩???
- faceMaskFrames??????8媛쒕줈 媛숆퀬, 理쒖쟻??寃쎈줈??以묎컙 ?꾨젅??4媛쒕? 蹂닿컙?덈떎.
- ?먮룞 寃異?total? ??73.7珥덉뿉????26.2珥덈줈 以꾩뿀??
- ?먮낯 ?댁긽???좎? ?곹깭?먯꽌 parallel detector 2媛쒕? ?곕㈃ faceMaskFrames 8媛쒕? ?좎??섎㈃???먮룞 寃異?total????20.8珥덇퉴吏 以꾩뿀?? `detectMs` ?⑷퀎??蹂묐젹 thread ?꾩쟻 ?쒓컙?대씪 wall-clock??`totalMs`瑜??곗꽑 ?먮떒?쒕떎.
- ?ㅻ쭔 `DetectEveryNFrames=2` 鍮꾧탳?먯꽌??baseline-only frame 27, optimized-only frame 87??諛쒖깮?덈떎. 怨듯넻 ?꾨젅??IoU???믪븯吏留??꾨젅???⑥쐞 ?꾩쟾 ?쇱튂???꾨땲誘濡??덉쭏 ?곗꽑 湲곕낯媛믪쑝濡쒕뒗 遺?곸젅?섎떎.
- 紐⑤뱺 ?꾨젅??寃異쒖쓣 ?좎???`parallel=2` 寃쎈줈??baseline怨?`baselineFrames=8`, `optimizedFrames=8`, `common=8`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`?쇰줈 ?꾩쟾 ?쇱튂?덈떎.
- ??湲곕낯 議고빀??`UseTracking=true + DetectEveryNFrames=1 + parallel=2`??`pipe-parallel`濡?吏꾩엯?덇퀬, baseline怨??꾨젅??諛뺤뒪媛 ?꾩쟾 ?쇱튂?덈떎.
- ?곕씪??湲곕낯 ?덉쭏 寃쎈줈??`DetectEveryNFrames=1 + parallel pipeline`?쇰줈 ?붾떎.
- `DetectEveryNFrames=3`? ??吏㏃? 援ш컙?먯꽌 ??鍮⑤옄吏留? 鍮좊Ⅸ ?吏곸엫/?λ㈃ ?꾪솚?먯꽌 蹂닿컙 ?섏〈?꾧? 而ㅼ????덉쭏 由ъ뒪?ш? ?덉쑝誘濡?湲곕낯媛믪쑝濡??곸슜?섏? ?딅뒗??
- 0.75 ?ㅼ슫?ㅼ??쇱? faceMaskFrames 8媛쒕? ?좎??섎㈃???먮룞 寃異?total????19.9珥덇퉴吏 以꾩???
- 0.5 ?ㅼ슫?ㅼ??쇱? ??鍮좊Ⅴ吏留?faceMaskFrames媛 6媛쒕줈 以꾩뼱 ?덉쭏 ?먯떎???뺤씤?섏뼱 湲곕낯媛??꾨낫?먯꽌 ?쒖쇅?덈떎.
- export??direct face rect 寃쎈줈瑜??ъ슜?덇퀬, ??耳?댁뒪 紐⑤몢 ?꾨즺?먮떎.
- ??smoke??吏㏃? 援ш컙 湲곗??대?濡??꾩껜 17遺??곸긽??理쒖쥌 ?섏튂濡??쇰컲?뷀븯硫????쒕떎.

湲곕낯媛??먮떒:

- ?좉퇋/珥덇린 ?먮룞 紐⑥옄?댄겕 downscale 湲곕낯媛믪? `1.0 + BalancedBilinear`濡??좎??쒕떎.
- parallel session 湲곕낯媛믪? 湲곗〈泥섎읆 `2`瑜??좎??쒕떎. ?ㅼ젣 smoke?먯꽌 ?먮낯 ?댁긽???덉쭏???좎???梨?wall-clock 媛쒖꽑???뺤씤?먭퀬, ?먮룞 ?쒕꼫媛 ?λ퉬蹂꾨줈 ???섏? ?몄뀡 ?섎? 怨좊? ???덈떎.
- `DetectEveryNFrames` 湲곕낯媛믪? `1`濡??좎??쒕떎. `2`? `3`? 異붽? ?덉쭏 寃利??꾧퉴吏 ?ъ슜???좏깮/?ㅽ뿕 ?듭뀡?쇰줈留??붾떎.
- `UseTracking=true`?쇰룄 `DetectEveryNFrames=1`?대㈃ 紐⑤뱺 ?꾨젅??寃異쒖씠誘濡?parallel all-frame pipeline???ъ슜?????덇쾶 ?덈떎.
- `AutoSettingsState.SettingsVersion=3`???ъ슜??援щ쾭??????ㅼ젙? ?덉쭏 ?곗꽑 湲곕낯媛믪쑝濡?蹂댁젙?쒕떎.
- `0.75`????援ш컙 smoke?먯꽌 ?띾룄 ?대뱷怨??숈씪 faceMaskFrames瑜?蹂댁?吏留? ?묒? ?쇨뎬/痢〓㈃ ?쇨뎬/癒??쇨뎬 ?덉쭏???꾩껜 ?곸긽?먯꽌 蹂댁옣?섏? 紐삵븯誘濡?湲곕낯媛믪쑝濡??곸슜?섏? ?딅뒗??
- `0.5`???띾룄???좊━?섏?留??ㅼ젣 smoke?먯꽌 寃異??꾨젅???섍? 以꾩뿀?쇰?濡?湲곕낯媛믪쑝濡??곸슜?섏? ?딆븯??

?ㅼ젣 6遺?援ш컙 5珥??대┰ 異붽? smoke:

| 耳?댁뒪 | 寃쎈줈 | 寃異??몄텧 | faceMaskFrames | ?먮룞 寃異?total | export total | 鍮꾧탳 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| baseline-all-frames | `pipe-single` | 150 | 8 | 100,254ms | 9,930ms | 湲곗? |
| optimized-track-1-parallel-2 | `pipe-parallel` | 150 | 8 | 53,724ms | 10,648ms | `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000` |
| script-default-track-1-parallel-2 | `pipe-parallel` | 150 | 8 | 56,451ms | 11,121ms | baseline 鍮꾧탳 ?앸왂, 湲곕낯 ?ㅽ겕由쏀듃 寃쎈줈 ?뺤씤 |

?댁꽍:

- 5珥?150?꾨젅???대┰?먯꽌??紐⑤뱺 ?꾨젅??寃異쒖쓣 ?좎???parallel pipeline? baseline怨??꾨젅??諛뺤뒪媛 ?꾩쟾???쇱튂?덈떎.
- ?먮룞 寃異?wall-clock? ??100.3珥덉뿉??53.7珥덈줈 以꾩뿀??
- smoke ?ㅽ겕由쏀듃 湲곕낯媛믩룄 `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `ParallelDetectorCount=2`濡?留욎톬怨? 湲곕낯 ?ㅽ뻾??`pipe-parallel`濡?吏꾩엯?섎뒗 寃껋쓣 ?뺤씤?덈떎.
- `[AutoRunSummary]`??detector ?쒓린??ONNX Runtime provider瑜??ы븿??`FaceOnnxDetector/CPU`, `FaceOnnxDetector/GPU:DirectML`泥섎읆 ?ㅼ젣 媛??寃쎈줈瑜??뺤씤?????덇쾶 ?덈떎.
- export????蹂묐ぉ???꾨땲硫? ?꾩옱 蹂묐ぉ? ?ъ쟾???먮룞 寃異?detector ?ㅽ뻾?대떎.

?ㅼ젣 9遺?援ш컙 2珥??대┰ 異붽? smoke:

| 耳?댁뒪 | 寃쎈줈 | 寃異??몄텧 | faceMaskFrames | ?먮룞 寃異?total | export total | 鍮꾧퀬 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| script-default-track-1-parallel-2-cpu | `pipe-parallel` | 60 | 51 | 24,220ms | 12,828ms | `FaceOnnxDetector/CPU`, ?쇨뎬 ?ㅼ닔 援ш컙 |
| script-default-track-1-parallel-2-gpu | `pipe-parallel` | 60 | 51 | 19,650ms | 13,101ms | `FaceOnnxDetector/GPU:DirectML`, 湲곕낯 ?ㅽ겕由쏀듃 寃쎈줈 |

?댁꽍:

- ?ㅻⅨ ?쒓컙??먯꽌??湲곕낯 寃쎈줈???먮낯 ?댁긽?? 紐⑤뱺 ?꾨젅??寃異? parallel pipeline?쇰줈 吏꾩엯?덈떎.
- ?대떦 援ш컙? faceMaskFrames媛 51媛쒕씪 6遺?援ш컙蹂대떎 ?쇨뎬 寃異쒖씠 ?⑥뵮 留롮? ?λ㈃?쇰줈 蹂댁씠硫? export??direct face rect 51?꾨젅?꾩쑝濡??꾨즺?먮떎.
- 媛숈? 9遺?援ш컙?먯꽌 DirectML? CPU ?鍮??먮룞 寃異?wall-clock????24.2珥덉뿉??19.7珥덈줈 以꾩?怨? faceMaskFrames??51媛쒕줈 ?좎??먮떎.
- ???먮룞 ?ㅼ젙 踰꾩쟾??`3`?쇰줈 ?щ젮 legacy ????ㅼ젙? Windows/macOS?먯꽌 GPU ?ъ슜 湲곕낯媛믪쓣 ?ㅼ떆 ?곸슜?쒕떎. GPU 珥덇린???ㅽ뙣 ??湲곗〈 detector fallback 濡쒖쭅??CPU濡??대젮媛꾨떎.
- legacy ????ㅼ젙? `DetectEveryNFrames=1`, `DownscaleRatio=1.0`, GPU 湲곕낯媛믩퓧 ?꾨땲??`ParallelSessionCount`??理쒖냼 2濡?蹂댁젙?쒕떎.
- direct face blur??alpha/radius map ?ъ쟾 怨꾩궛 理쒖쟻?붾룄 ?쒕룄?덉?留? 媛숈? smoke?먯꽌 `maskMs`媛 ??5.1珥덉뿉??8.5珥덈줈 ?낇솕?섏뼱 諛섏쁺?섏? ?딆븯??
- smoke ?ㅽ겕由쏀듃??`-SkipTrim`?쇰줈 ?щ윭 ?대┰???뚯뒪?명븷 ??異쒕젰 ?뚯씪????씠吏 ?딅룄濡??낅젰 ?대┰ ?대쫫 湲곕컲?쇰줈 export ?뚯씪紐낆쓣 留뚮뱺??
- smoke ?ㅽ겕由쏀듃??`-SkipExport`瑜?異붽??덈떎. 湲?援ш컙?먯꽌???먮룞 寃異?face rect 鍮꾧탳留?癒쇱? ?뺤씤?섍퀬, export?????援ш컙?먯꽌 ?곕줈 寃利앺븷 ???덈떎.
- smoke ?ㅽ겕由쏀듃??`-UseAutoTune`瑜?異붽??덈떎. ?ㅼ젣 `DetectorAutoTuner.TryTune` 寃쎈줈瑜??몄텧???쒕떇 ?쇰꺼/?몄뀡/provider瑜??뺤씤?????덈떎.
- smoke ?ㅽ겕由쏀듃???덉쭏 gate瑜?異붽??덈떎. baseline 鍮꾧탳 ?ㅽ뻾 ???꾨젅???꾨씫/異붽?, 諛뺤뒪 ??李⑥씠, `avgBestIou`, `minBestIou` 湲곗???寃?ы븯怨??ㅽ뙣?섎㈃ exit code 2濡?醫낅즺?쒕떎.
- `scripts/verify-native-publish.ps1`瑜?異붽??덈떎. `win-x64`? `osx-arm64` publish瑜??ㅽ뻾?섍퀬 媛?runtime???꾩닔 native ?뚯씪??寃?ы븳?? macOS 寃利앹뿉?쒕뒗 Windows `onnxruntime.dll`/`onnxruntime_providers_shared.dll`???욎씠硫??ㅽ뙣?섍퀬, `libomp.dylib` ?꾨씫? 寃쎄퀬濡??쒖떆?쒕떎.
- `scripts/verify-auto-mosaic-default.ps1`瑜?異붽??덈떎. 湲곕낯 ?ㅽ뻾? 6遺?3珥??대┰?먯꽌 CPU single baseline ?鍮?CPU `pipe-parallel(2)` all-frame ?덉쭏 gate瑜?寃?ы븯怨? 6遺?5珥??대┰?먯꽌 `UseAutoTune` 湲곕낯 寃쎈줈媛 `GPU:DirectML`, `pipe-parallel(2)`, ???꾨젅??寃異쒕줈 ?숈옉?섎뒗吏 ?뺤씤?쒕떎. `-RunLongAutoTune`??遺숈씠硫?12遺?30珥??대┰源뚯? ?뺤옣 寃利앺븳??

?ㅼそ ?쒓컙? 異붽? smoke:

| 援ш컙 | 寃쎈줈 | 寃異??몄텧 | faceMaskFrames | ?먮룞 寃異?total | export total | 鍮꾧퀬 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 12:00 2珥?| `GPU:DirectML` / `pipe-parallel` | 60 | 28 | 25,842ms | 10,057ms | direct face rect export |
| 15:00 2珥?| `GPU:DirectML` / `pipe-parallel` | 59 | 0 | 18,315ms | 193ms | ?쇨뎬 ?놁쓬, remux-copy |

?댁꽍:

- 12遺?援ш컙???먮낯 ?댁긽?? 紐⑤뱺 ?꾨젅??寃異? DirectML, parallel pipeline?쇰줈 ?숈옉?덇퀬 ?쇨뎬 留덉뒪??28?꾨젅?꾩쓣 export?덈떎.
- 15遺?援ш컙? 寃異??꾨젅?꾩씠 ?놁뼱 blur ??곸씠 ?놁뿀怨? remux-copy 寃쎈줈媛 `[ExportRunSummary]`瑜??④린硫?鍮좊Ⅴ寃?醫낅즺?먮떎.

12遺?援ш컙 baseline 鍮꾧탳:

| 耳?댁뒪 | provider / 寃쎈줈 | 寃異??몄텧 | faceMaskFrames | ?먮룞 寃異?total | export total |
| --- | --- | ---: | ---: | ---: | ---: |
| baseline-all-frames | `CPU` / `pipe-single` | 60 | 28 | 27,579ms | 7,206ms |
| optimized-track-1-gpu | `GPU:DirectML` / `pipe-parallel` | 60 | 28 | 20,839ms | 7,746ms |

鍮꾧탳 寃곌낵:

- `baselineFrames=28`, `optimizedFrames=28`, `common=28`, `onlyBaseline=0`, `onlyOptimized=0`, `boxCountDiffFrames=0`
- `avgBestIou=0.916`, `minBestIou=0.846`
- DirectML provider??floating point 李⑥씠濡?諛뺤뒪 醫뚰몴媛 ?꾩쟾 ?숈씪?섏????딆?留? ?숈씪 ?꾨젅???숈씪 諛뺤뒪 ?섎? ?좎??덇퀬 IoU???믪? ?몄씠??
- ?먮룞 寃異?wall-clock? 媛숈? 12遺?援ш컙?먯꽌 ??27.6珥덉뿉??20.8珥덈줈 以꾩뿀??
- `DetectorAutoTuner`??GPU ?꾨낫 quality gate瑜?異붽??덈떎. ?쒕떇 ?섑뵆?먯꽌 CPU 湲곗?怨?諛뺤뒪 ?섍? ?ㅻⅤ嫄곕굹 理쒖냼 IoU媛 `0.75` 誘몃쭔?대㈃ GPU ?꾨낫???띾룄媛 鍮⑤씪???좏깮?섏? ?딅뒗??
- 12遺?2珥??대┰?먯꽌 `-UseAutoTune` smoke瑜??ㅽ뻾?덇퀬, ?쒕꼫媛 `GPU 2?몄뀡/8?ㅻ젅??瑜??좏깮????`FaceOnnxDetector/GPU:DirectML`, `pipe-parallel(2)`濡??먮룞 寃異쒖쓣 ?꾨즺?덈떎.
- ?쒕꼫 罹먯떆 ?ㅼ뿉 `IntraOpNumThreads`? `EnablePreprocessParallelism`???ы븿?? ?ъ슜?먭? thread/preprocess ?ㅼ젙??諛붽엥?????댁쟾 ?쒕떇 寃곌낵媛 ?섎せ ?ъ궗?⑸릺吏 ?딄쾶 ?덈떎.
- 吏㏃? ?쒕떇 ?섑뵆?먯꽌 `1?몄뀡`???좏깮?섎㈃ 湲?援ш컙?먯꽌 ?ㅽ엳???먮젮吏???ㅼ륫???덉뼱, ?쒕꼫???ъ슜?먭? ?좏깮??蹂묐젹 ?몄뀡 ?섎? ??텛吏 ?딄퀬 ?대떦 ?몄뀡 ???덉뿉??thread/provider留?怨좊Ⅴ寃??덈떎.
- 蹂댁젙 ??`-UseAutoTune` smoke?먯꽌 ?ㅼ떆 `GPU 2?몄뀡/8?ㅻ젅??, `pipe-parallel(2)`媛 ?좏깮?섎뒗 寃껋쓣 ?뺤씤?덈떎.
- 6遺?5珥??대┰?먯꽌??`-UseAutoTune -SkipBaseline -SkipExport`瑜??ㅼ떆 ?ㅽ뻾?덇퀬, ?쒕꼫媛 `GPU 2?몄뀡/8?ㅻ젅??瑜??좏깮????`FaceOnnxDetector/GPU:DirectML`, `pipe-parallel(2)`濡??먮룞 寃異쒖쓣 ?꾨즺?덈떎.
- ?대떦 5珥??대┰??異붽? 寃곌낵??`processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=8`, `totalMs=66,709ms`??? export???섎룄?곸쑝濡??앸왂?덈떎.
- 12遺?30珥??대┰?먯꽌 `-UseAutoTune -SkipBaseline -SkipExport`瑜??ㅽ뻾?덉쓣 ???쒕꼫媛 CPU `ORT_PARALLEL`??怨좊Ⅴ??耳?댁뒪瑜??뺤씤?덈떎. ??寃쎈줈??`FaceOnnxDetector/CPU`, `pipe-parallel(2)`, `processed=899`, `faceMaskFrames=309`, `totalMs=499,482ms`濡?湲곗〈 GPU 湲곕낯 寃쎈줈蹂대떎 ?먮졇??
- ?먯씤? 吏㏃? ?쒕떇 ?섑뵆 throughput???κ린 援ш컙 wall-clock怨??닿툔?섎뒗 寃쎌슦??? GPU ?꾨낫媛 quality gate瑜??듦낵?섍퀬 CPU 理쒓퀬 ?먯닔??75% ?댁긽?대㈃ GPU瑜??좎??섎룄濡??쒕꼫 ?뺤콉??蹂댁젙?덈떎.
- 蹂댁젙 ??媛숈? 12遺?30珥??대┰?먯꽌 ?쒕꼫媛 `GPU 2?몄뀡/8?ㅻ젅??瑜??좏깮?덇퀬, `FaceOnnxDetector/GPU:DirectML`, `pipe-parallel(2)`, `processed=899`, `detects=899`, `interpolated=0`, `faceMaskFrames=309`, `totalMs=384,784ms`濡??꾨즺?덈떎.
- smoke script??`[SmokeTune]` provider ?쒓린媛 CPU ?좏깮 ???댁쟾 GPU ?쒕룄 ?곹깭瑜?蹂댁뿬以????덉뼱, CPU ?좏깮?대㈃ provider瑜?`CPU`濡?異쒕젰?섎룄濡?怨좎낀??

12遺?援ш컙 10珥?湲곕낯 GPU smoke:

| 援ш컙 | provider / 寃쎈줈 | 寃異??몄텧 | faceMaskFrames | ?먮룞 寃異?total | export total |
| --- | --- | ---: | ---: | ---: | ---: |
| 12:00 10珥?| `GPU:DirectML` / `pipe-parallel` | 300 | 127 | 109,771ms | 30,639ms |
| 12:00 10珥?| `GPU:DirectML` / `pipe-single` + ROI | 300 | 129 | 138,857ms | 34,996ms |
| 12:00 30珥?| `GPU:DirectML` / `pipe-parallel` | 899 | 309 | 338,306ms | skipped |
| 06:00 30珥?| `GPU:DirectML` / `pipe-parallel` | 899 | 44 | 377,052ms | skipped |

?댁꽍:

- 2珥??섑뵆蹂대떎 湲?300?꾨젅??援ш컙?먯꽌???먮낯 ?댁긽?? 紐⑤뱺 ?꾨젅??寃異? DirectML, parallel pipeline???꾨즺?먮떎.
- `interpolated=0`?대?濡?湲곕낯 ?덉쭏 寃쎈줈???꾨젅???ㅽ궢/蹂닿컙 ?놁씠 ???꾨젅??寃異쒖씠??
- export??direct face rect 127?꾨젅?꾩쓣 泥섎━?덇퀬 ?꾨즺?먮떎.
- `parallel=1`??ROI ?⑥씪 ?뚯씠?꾨룄 痢≪젙?덉?留??먮룞 寃異?wall-clock??109.8珥덉뿉??138.9珥덈줈 ?먮젮議뚭퀬 faceMaskFrames??127?먯꽌 129濡??щ씪議뚮떎.
- ?곕씪??湲곕낯 ?띾룄/?덉쭏 寃쎈줈??ROI ?⑥씪 ?뚯씠?꾧? ?꾨땲??`parallel=2` all-frame pipeline?쇰줈 ?좎??쒕떎.
- 12遺?6遺?30珥? 媛?899?꾨젅??援ш컙??export ?놁씠 ?먮룞 寃異쒖쓣 ?꾨즺?덇퀬, `interpolated=0`?쇰줈 ???꾨젅??寃異쒖쓣 ?좎??덈떎.

?꾩껜 ?곸긽 ?덉긽:

- ?먮낯 `srcTest/260102_jp_10.mp4`??3840x2160, 29.97fps, 1,067.6珥? 31,996?꾨젅?꾩씠??
- 12遺?30珥?援ш컙? 899?꾨젅???먮룞 寃異쒖뿉 338,306ms, 6遺?30珥?援ш컙? 899?꾨젅???먮룞 寃異쒖뿉 377,052ms媛 嫄몃졇??
- ?⑥닚 ?섏궛 ???꾩껜 ???꾨젅???먮룞 寃異쒖? ??3.3~3.7?쒓컙 踰붿쐞濡??덉긽?쒕떎. export ?쒓컙? 蹂꾨룄??
- ?곕씪??理쒖긽 ?덉쭏 湲곕낯媛믪? ???꾨젅??寃異쒖쓣 ?좎??섎릺, ?꾩껜 ?곸긽 ?ㅼ궗???띾룄 媛쒖꽑? ?ν썑 ??detector backend ?먮뒗 ?덉쭏 寃利앸맂 sparse/refiner 援ъ“媛 ?꾩슂?섎떎.
- ?꾩옱 寃利앸맂 湲곕낯媛믪? ?덉쭏 ?곗꽑 寃쎈줈?닿퀬, `DetectEveryNFrames > 1` ?먮뒗 downscale? ?꾩껜 ?덉쭏 洹쇨굅媛 遺議깊븯誘濡?湲곕낯媛믪쑝濡??밴꺽?섏? ?딅뒗??

紐⑤뜽 援먯껜 媛?μ꽦 ?뺤씤:

- ?꾩옱 `FaceONNX` NuGet ?⑦궎吏??蹂꾨룄 `.onnx` ?뚯씪???꾨줈?앺듃???몄텧?섏? ?딄퀬 DLL ?대? 由ъ냼?ㅻ줈 紐⑤뜽???ы븿?섎뒗 援ъ“??
- XML 臾몄꽌 湲곗? `FaceONNX.FaceDetector`??怨듦컻 ?앹꽦?먮뒗 threshold? `SessionOptions` 以묒떖?대ŉ, ?꾩쓽 紐⑤뜽 寃쎈줈瑜?二쇱엯?섎뒗 ?앹꽦?먮뒗 ?뺤씤?섏? ?딆븯??
- DLL 臾몄옄??湲곗? ?꾩옱 detector 紐⑤뜽 由ъ냼?ㅻ뒗 `deploy_dpe_220_v4_slim.onnx`濡?蹂댁씤??
- ?꾩옱 ?묒뾽怨듦컙怨?濡쒖뺄 NuGet cache?먮뒗 諛붾줈 遺숈씪 ???덈뒗 蹂꾨룄 face/yolo/opencv detector 紐⑤뜽 ?먮뒗 ?⑦궎吏媛 ?뺤씤?섏? ?딆븯?? ?ㅽ듃?뚰겕??蹂꾨룄 紐⑤뜽 ?뚯씪 ?놁씠 ??detector backend瑜?援ы쁽?섎㈃ ?꾩쓽 異붿젙 援ы쁽???섎?濡?湲곕낯 寃쎈줈?먮뒗 ?ｌ? ?딅뒗??
- ?곕씪???④린 媛쒖꽑? `FaceOnnxDetector` ?좎? + DirectML/CoreML/CPU provider + pipeline 理쒖쟻?붽? 留욊퀬, ?ㅼ젣 紐⑤뜽 援먯껜??`IBgraFaceDetector` 援ы쁽???덈줈 異붽??섎뒗 backend ?뺤옣?쇰줈 吏꾪뻾?댁빞 ?쒕떎.
- ?먮룞 ?ㅽ뻾 寃쎈줈?먯꽌 `_detectorFactoryOptions`媛 `FaceDetectorFactoryOptions.ForOnnx(...)`濡??ㅼ떆 怨좎젙?섎뜕 遺遺꾩쓣 ?쒓굅?덈떎. ?댁젣 ?먮룞 ?쒕떇? `FaceOnnx` backend?먯꽌留??곸슜?섍퀬, factory option ?먯껜???좎??섎?濡???backend瑜?異붽??덉쓣 ???먮룞 紐⑥옄?댄겕 寃쎈줈媛 ?ㅼ떆 FaceONNX濡??섎룎?꾧?吏 ?딅뒗??
- `ScrfdOnnxDetector` backend瑜?異붽??덈떎. ??backend??insightface 怨꾩뿴 SCRFD ONNX 異쒕젰(score/bbox stride map)???댁꽍?섎뒗 ?몃? 紐⑤뜽 寃쎈줈 湲곕컲 `IBgraFaceDetector` 援ы쁽?대떎. 湲곕낯媛믪쑝濡쒕뒗 ?ъ슜?섏? ?딆쑝硫? 紐⑤뜽 ?뚯씪???놁쑝硫?紐낇솗???ㅽ뙣?쒕떎.
- `scripts/run-srcTest-smoke.ps1 -ScrfdModelPath <model.onnx>`瑜?異붽??덈떎. baseline? 湲곗〈 FaceONNX濡??좎??섍퀬 optimized case留?SCRFD backend濡??ㅽ뻾??媛숈? quality gate?먯꽌 A/B 鍮꾧탳?????덈떎. ?꾩옱 ??μ냼?먮뒗 SCRFD 紐⑤뜽 ?뚯씪???놁쑝誘濡??ㅼ젣 SCRFD ?덉쭏/?띾룄 ?섏튂???꾩쭅 ?녿떎.
- `scripts/inspect-onnx-outputs.ps1`瑜?異붽??덈떎. ?몃? ONNX 紐⑤뜽??input/output ?대쫫, shape, 媛?踰붿쐞瑜??뺤씤??score/bbox/kps 異쒕젰 洹쒖빟??decoder 援ы쁽 ?꾩뿉 寃利앺븳??
- SCRFD ?꾩쿂由??듭뀡?쇰줈 `UseLetterboxResize`, `UseRgbInput`, `MultiplyBboxByStride`瑜?異붽??덈떎. smoke script?먯꽌??`-ScrfdStretchInput`, `-ScrfdUseBgr`濡?letterbox/stretch? RGB/BGR 議고빀??鍮꾧탳?????덈떎.

## 2026-05-11 ?꾨즺 媛먯궗
紐⑺몴瑜??ㅼ쓬 deliverable濡??섎닠 ?뺤씤?쒕떎.

| ?붽뎄 | ?꾩옱 利앷굅 | ?곹깭 |
| --- | --- | --- |
| ?먮룞 紐⑥옄?댄겕 ?덉쭏 ???諛⑹? | 湲곕낯媛믪쓣 `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `interpolated=0` 寃쎈줈濡??좎??덇퀬, 6遺?3珥?5珥?諛?12遺?2珥?鍮꾧탳?먯꽌 baseline ?鍮??꾨씫 ?꾨젅??0媛쒕? ?뺤씤?덈떎. | 遺遺?異⑹” |
| ?먮룞 寃異??띾룄 媛쒖꽑 | CPU single baseline ?鍮?`pipe-parallel(2)`? DirectML 湲곕낯 寃쎈줈?먯꽌 3珥?5珥?2珥??섑뵆??wall-clock 媛먯냼瑜??뺤씤?덈떎. | 遺遺?異⑹” |
| ?ㅼ젣 `srcTest` ?곸긽 湲곕컲 寃利?| `srcTest/260102_jp_10.mp4`?먯꽌 ?щ윭 吏㏃? ?대┰怨?30珥??먮룞 寃異?smoke瑜??ㅽ뻾?덈떎. ?꾩껜 17遺???곗? ?꾩쭅 ?섑뻾?섏? ?딆븯?? | 誘몄셿猷?|
| 紐⑤뜽 援먯껜 媛?μ꽦 寃??| ?꾩옱 FaceONNX 紐⑤뜽? DLL ?대? 由ъ냼??援ъ“???꾩쓽 紐⑤뜽 寃쎈줈 援먯껜媛 遺덇??ν븯怨? ??紐⑤뜽? `IBgraFaceDetector` backend 異붽?媛 ?꾩슂?섎떎怨??뺤씤?덈떎. ?먮룞 ?ㅽ뻾 寃쎈줈媛 `_detectorFactoryOptions`瑜?蹂댁〈?섎룄濡??섏젙??backend 援먯껜 吏?먯? 留됲엳吏 ?딄쾶 ?덈떎. | ?ㅺ퀎 異⑹”, 援ы쁽 誘몄셿猷?|
| 蹂묐ぉ 痢≪젙 媛?μ꽦 | `[AutoRunSummary]`, `[ExportRunSummary]`, `runId`, provider ?쒓린瑜?異붽??덈떎. | 異⑹” |
| ?먮룞 ?쒕떇 ?덉젙??| cancellation token, GPU quality gate, legacy ?ㅼ젙 migration, 蹂묐젹 ?몄뀡 ?좎? ?뺤콉??諛섏쁺?덇퀬 `-UseAutoTune` smoke?먯꽌 `GPU 2?몄뀡/8?ㅻ젅??媛 ?좎??⑥쓣 ?뺤씤?덈떎. | 異⑹” |
| export 寃쎈줈 媛쒖꽑 | direct face rect summary? no-blur remux-copy summary瑜?異붽??덈떎. hybrid copy???ㅼ젣 smoke?먯꽌 `Invalid argument`媛 諛쒖깮??鍮꾪솢?깊솕?덈떎. | 遺遺?異⑹” |
| ?덉쭏 寃??UX | ?먮룞 ?댁긽 ?꾨젅?꾩? ?꾩껜 no-face ?꾨젅?꾩씠 ?꾨땲???욌뮘 ?쇨뎬 ?ъ씠??吏㏃? no-face gap, ??? confidence, flicker 以묒떖?쇰줈 醫곹삍?? | 異⑹” |
| Windows 諛고룷 寃利?| `scripts/verify-native-publish.ps1 -RuntimeIdentifier win-x64`媛 ?깃났?덇퀬, publish ?대뜑??`FaceShield.exe`, `DirectML.dll`, `onnxruntime.dll`, `FaceONNX.dll`, FFmpeg DLL?ㅼ씠 ?ы븿??寃껋쓣 ?뺤씤?덈떎. | 異⑹” |
| macOS 諛고룷 寃利?| Windows?먯꽌 `scripts/verify-native-publish.ps1 -RuntimeIdentifier osx-arm64` cross-publish媛 ?깃났?덇퀬, `FaceShield`, `libonnxruntime.dylib`, FFmpeg dylib?ㅼ씠 ?ы븿??寃껋쓣 ?뺤씤?덈떎. Windows DirectML package媛 osx publish???욎뿬 `NETSDK1152`媛 ?섎뜕 臾몄젣瑜?package condition ?섏젙?쇰줈 ?닿껐?덇퀬, Windows `onnxruntime.dll`/`onnxruntime_providers_shared.dll` 遺?щ룄 寃利앺뻽?? ?ㅻ쭔 ?꾩옱 publish?먮뒗 `libomp.dylib`媛 ?ы븿?섏? ?딆쑝誘濡?warning???④린硫? ?ㅼ젣 Mac ?고????뺤씤? ?⑥븘 ?덈떎. | 遺遺?異⑹” |

?꾨즺濡?蹂????녿뒗 ??ぉ:

- ?꾩껜 17遺??먮낯 ?곸긽 end-to-end ?먮룞 寃異?+ export ??곗씠 ?꾩쭅 ?녿떎.
- ?몃? SCRFD detector backend??異붽??먯?留? ?ㅼ젣 紐⑤뜽 ?뚯씪???놁뼱 SCRFD A/B ?ㅽ뻾 寃곌낵???꾩쭅 ?녿떎.
- GUI?먯꽌 ?닿린, preview, ?먮룞 寃異? ?섎룞 ?섏젙, export ?꾩껜 ?먮쫫??吏곸젒 smoke?섏? ?딆븯??
- macOS ?ㅺ린湲곗뿉???먮룞 紐⑤뱶 ?쒖옉怨?ONNX/libomp runtime load瑜??뺤씤?섏? ?딆븯??

?곕씪???꾩옱 ?곹깭??理쒖긽 ?덉쭏 湲곕낯 寃쎈줈? 痢≪젙 媛?ν븳 怨좎냽 寃쎈줈瑜?援ы쁽???④퀎?대ŉ, goal ?꾨즺濡?泥섎━?섏? ?딅뒗??

異붽? gate 寃利?

- 6遺?3珥??대┰?먯꽌 CPU single baseline怨?CPU `pipe-parallel(2)` all-frame 寃쎈줈瑜?`MinAvgIou=0.99`, `MinBestIou=0.99`濡?鍮꾧탳?덇퀬 `SmokeQualityGate passed=True`瑜??뺤씤?덈떎.
- 媛숈? ?대┰?먯꽌 `DetectEveryNFrames=2` sparse 寃쎈줈??`onlyBaseline=27`, `onlyOptimized=87`??諛쒖깮??`SmokeQualityGate passed=False`媛 ?섏뿀怨? ?ㅽ겕由쏀듃媛 exit code 2濡?醫낅즺?섎뒗 寃껋쓣 ?뺤씤?덈떎.
- `scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾???듦낵?덈떎. ???ㅽ뻾?먯꽌 ?덉쭏 gate??`avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`?怨? auto tune 吏㏃? 寃利앹? `GPU 2?몄뀡/8?ㅻ젅??, `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=8`, `totalMs=64,236ms`濡??꾨즺?덈떎.

## 2026-05-12 ?ㅼ젣 ?ъ슜 ?뺤씤 ?곹깭
?ъ슜?먭? ?꾩옱 ???곹깭瑜??ㅼ젣 ?곸긽?먯꽌 ?뺤씤??寃곌낵???ㅼ쓬怨?媛숇떎.

- ???쇨뎬怨??쇰컲?곸씤 ?쇨뎬 寃異? 紐⑥옄?댄겕???곷떦???숈옉?쒕떎.
- ?묒? ?쇨뎬? 紐⑥옄?댄겕媛 ?????섎뒗 援ш컙???덈떎.
- ?щ엺???꾨땶 臾쇨굔???쇨뎬濡??ㅺ?異쒕릺??紐⑥옄?댄겕?섎뒗 寃쎌슦媛 ?덈떎.
- 紐⑥옄?댄겕媛 ?쇨뎬???곕씪 ?몃옒?밸릺???먮굦??遺議깊븯怨? 以묎컙??源쒕컯嫄곕━??援ш컙???덈떎.
- export ?쒓컙???ъ쟾???ㅻ옒 嫄몃┛??

??愿李곗? 湲곗〈 smoke gate???쒓퀎瑜?蹂댁뿬以?? 湲곗〈 gate??吏㏃? ?대┰?먯꽌 CPU baseline怨?optimized 寃쎈줈???꾨젅??諛뺤뒪 ?쇱튂 ?щ?瑜?蹂대뒗 ?곕뒗 ?좏슚?덉?留? ?ㅼ젣 ?ъ슜 ?덉쭏 愿?먯쓽 ?묒? ?쇨뎬, ?ㅽ깘 臾쇱껜, track continuity, flicker, 湲?export ?쒓컙??異⑸텇????쒗븯吏 紐삵뻽??

?꾩옱 ?곗꽑?쒖쐞???ㅼ쓬?쇰줈 議곗젙?쒕떎.

1. ?묒? ?쇨뎬 誘명깘 援ш컙??frame index? ?붾㈃ ?꾩튂濡??섏쭛?쒕떎.
2. 臾쇨굔 ?ㅽ깘 援ш컙??frame index? 媛앹껜 醫낅쪟濡??섏쭛?쒕떎.
3. 源쒕컯??援ш컙??`?댁쟾 ?쇨뎬 ?덉쓬 -> ?꾩옱 ?놁쓬 -> ?ㅼ쓬 ?쇨뎬 ?덉쓬` ?⑦꽩怨??ㅼ젣 ?곸긽 ?뺤씤 寃곌낵濡?遺꾨━?쒕떎.
4. track continuity瑜?媛뺥솕?쒕떎. ?⑥닚 ?꾨젅?꾨퀎 face rect dictionary留뚯쑝濡쒕뒗 遺議깊븯誘濡??쇨뎬蹂?track id, 吏㏃? gap 蹂닿컙, 諛뺤뒪 smoothing, ?ㅽ깘 track ?쒓굅媛 ?꾩슂?섎떎.
5. ?묒? ?쇨뎬 ??묒? threshold ?꾪솕留뚯쑝濡?泥섎━?섏? ?딅뒗?? threshold ?꾪솕??臾쇨굔 ?ㅽ깘???섎┫ ???덉쑝誘濡? ROI ?ш?異? 2李?verifier/refiner, ?먮뒗 ?묒? ?쇨뎬??媛뺥븳 detector backend瑜?鍮꾧탳?댁빞 ?쒕떎.
6. export 蹂묐ぉ? `[ExportRunSummary]`??`maskMs`, `swsToBgraMs`, `swsToEncMs`, `encodeMs`, `totalMs`瑜??ㅼ젣 湲??곸긽?먯꽌 ?뺤씤????????ぉ遺??以꾩씤??

異붽? 諛⑺뼢: ?뺤젙 track 以묒떖?쇰줈 紐⑥옄?댄겕瑜??좎??쒕떎.

- ??踰??쇨뎬濡??뺤젙????곸? detector媛 ?좉퉸 ?볦퀜???곸긽?먯꽌 ?щ씪吏??뚭퉴吏 媛?ν븳 ??釉붾윭瑜??좎??쒕떎.
- ?? 泥섏쓬 1?꾨젅?꾨쭔 ?ㅺ?異쒕맂 臾쇨굔???앷퉴吏 釉붾윭?섎㈃ ???섎?濡?紐⑤뱺 ?꾨낫瑜?諛붾줈 ?뺤젙?섏? ?딅뒗??
- `Tentative`: ???꾨낫. 1?꾨젅??寃異쒕쭔?쇰줈???뺤젙?섏? ?딅뒗??
- `Confirmed`: ?쇱젙 ?꾨젅???숈븞 諛섎났 寃異쒕릺嫄곕굹 ?먯뿰?ㅻ윭???대룞/?ш린 蹂?붽? ?뺤씤???쇨뎬 track. 吏㏃? 誘명깘 援ш컙? 蹂닿컙?쒕떎.
- `Lost`: ?뺤젙 track???좉퉸 ?щ씪吏??곹깭. ?쇱젙 ?꾨젅?꾧퉴吏???덉륫/蹂닿컙?쇰줈 釉붾윭瑜??좎??쒕떎.
- `Ended`: ?붾㈃ 諛??대룞, 湲?誘명깘, scene cut, 鍮꾩젙?곸쟻???ш린/?꾩튂 蹂?붾줈 醫낅즺??track.
- 諛섏そ ?쇨뎬/媛?μ옄由??쇨뎬? 寃異?利앷굅媛 吏㏃븘???볦튂硫????섎?濡?`Tentative` ?곹깭?먯꽌 諛붾줈 踰꾨━吏 ?딅뒗??
- ?묒? 臾쇨굔 ?ㅽ깘? confidence媛 ?믪븘??吏㏃? ?⑤컻 track?대㈃ ?쒓굅?쒕떎.
- ?붾㈃ 以묒븰???묒? ?꾨낫?쇰룄 3?꾨젅???댁긽 ?먯뿰?ㅻ읇寃??댁뼱吏硫?諛붾줈 ?쒓굅?섏? ?딅뒗?? ?щ엺???ㅻ룎硫댁꽌 ?쇨뎬??諛섎쭔 蹂댁씠??寃쎌슦????踰붿쐞???ы븿?쒕떎.
- ?ν썑 援ы쁽? `FaceTrackState` ?먮뒗 `FaceTrackLifecycle` ?뺥깭濡??뺤옣?? frame dictionary ?꾩쿂由ш? ?꾨땲??track lifecycle 湲곗??쇰줈 理쒖쥌 face rect瑜?留뚮뱾?꾨줉 ?쒕떎.

?ㅼ쓬 援ы쁽 ?꾨낫:

- `FaceTrack`, `FaceTrackBuilder`, `FaceTrackInterpolator`瑜?異붽???frame ?⑥쐞 寃곌낵瑜?track ?⑥쐞濡??ш뎄?깊븳??
- 吏㏃? no-face gap? ?욌뮘 track??媛숈? ?쇨뎬???뚮쭔 蹂닿컙?쒕떎.
- ?쇱젙 湲몄씠 ?댄븯???⑤컻 ?ㅺ?異?track? ?쒓굅?섍굅???댁긽 ?꾨낫濡??쒖떆?쒕떎.
- ?묒? ?쇨뎬 ?꾨낫????? confidence?쇰룄 諛붾줈 踰꾨━吏 ?딄퀬 track ?꾨낫濡??좎????? ?곗냽?깆쑝濡??뺤젙?쒕떎.
- ?뺤젙??track? 吏㏃? detector 誘명깘?먮룄 `Lost` ?곹깭濡??좎??섍퀬, ?곸긽?먯꽌 ?щ씪吏??뚭퉴吏 蹂닿컙/?덉륫 釉붾윭瑜?吏?랁븳??
- 臾쇨굔 ?ㅽ깘? confidence留뚯쑝濡?援щ텇?섍린 ?대졄湲??뚮Ц?? box ?ш린/鍮꾩쑉/?吏곸엫/吏?띿떆媛?湲곕컲 ?꾪꽣? 2李?verifier瑜?寃?좏븳??
- export??face rect留??덈뒗 ?꾨젅?꾩뿉??direct blur 寃쎈줈瑜??좎??섎릺, 蹂??留덉뒪???몄퐫???쒓컙 以??ㅼ젣 蹂묐ぉ??癒쇱? 痢≪젙?쒕떎.

?꾨즺 湲곗????ㅼ쓬泥섎읆 蹂닿컯?쒕떎.

- ?묒? ?쇨뎬???ы븿?????援ш컙?먯꽌 誘명깘 frame ?섎? 湲곗〈蹂대떎 以꾩씤??
- 臾쇨굔 ?ㅽ깘 frame ?섎? 以꾩씤??
- 媛숈? ?쇨뎬 track??吏㏃? 源쒕컯?꾩쓣 以꾩씠怨? 紐⑥옄?댄겕 諛뺤뒪 ?대룞???꾨젅???ъ씠???먯뿰?ㅻ읇寃??댁뼱吏꾨떎.
- export???숈씪 ?덉쭏 議곌굔?먯꽌 `ExportRunSummary.totalMs` ?먮뒗 二쇱슂 蹂묐ぉ ??ぉ??媛먯냼?쒕떎.
- ????ぉ? 吏㏃? smoke媛 ?꾨땲???ㅼ젣 臾몄젣 ?곸긽?????援ш컙 ?щ윭 媛쒖뿉???뺤씤?쒕떎.

## 2026-05-12 track ?꾩쿂由?1李?援ы쁽
?ㅼ젣 ?ъ슜 ?뺤씤?먯꽌 ?섏삩 源쒕컯?꾧낵 ?⑤컻 ?ㅺ?異?臾몄젣瑜?以꾩씠湲??꾪빐 frame ?⑥쐞 蹂댁젙 濡쒖쭅 ?쇰?瑜?track ?⑥쐞 ?꾩쿂由щ줈 遺꾨━?덈떎.

異붽?/蹂寃??뚯씪:

- `Services/Analysis/FaceTrack.cs`
- `Services/Analysis/FaceTrackBuilder.cs`
- `Services/Analysis/FaceTrackInterpolator.cs`
- `ViewModels/Pages/WorkspaceViewModel.cs`
- `scripts/run-srcTest-smoke.ps1`

援ы쁽 ?댁슜:

- `FaceTrackBuilder`媛 frame蹂?face rect瑜?IoU, 以묒떖???대룞?? 硫댁쟻 蹂?붿쑉 湲곗??쇰줈 媛숈? ?쇨뎬 track?쇰줈 臾띕뒗??
- `FaceTrackInterpolator`媛 媛숈? track??吏㏃? no-face gap留?蹂닿컙?쒕떎.
- ?뺤젙 track? 留덉?留?寃異???detector媛 ?좉퉸 ?볦퀜??理쒕? 3?꾨젅?꾧퉴吏 ?대룞??湲곕컲?쇰줈 ?덉륫??釉붾윭瑜??좎??쒕떎.
- ?뺤젙 track lost-fill??諛쒖깮??frame index瑜?`FilledLostFrameIndices`? `lostFrames=` 濡쒓렇濡??④꺼, ?댄썑 ROI verifier/refiner瑜??대떦 frame 二쇰??먮쭔 遺숈씪 ???덇쾶 ?덈떎.
- `FaceTrackRoiRefiner`瑜?異붽???gap-fill/lost-fill ?꾨낫 ?꾨젅?꾨쭔 FFmpeg raw BGRA濡??ㅼ떆 ?쎄퀬, ?덉륫 諛뺤뒪 二쇰? ROI crop留?detector???ｌ뼱 ?ш?異쒗븳?? ROI 寃異?寃곌낵媛 ?덉륫 諛뺤뒪? 異⑸텇??媛源뚯슱 ?뚮쭔 湲곗〈 ?덉륫 諛뺤뒪瑜?援먯껜?쒕떎.
- ROI ?ш?異쒖? ?꾩뿭 detector threshold瑜?諛붽씀吏 ?딄퀬, ROI ?꾩슜 CPU FaceONNX detector?먯꽌留?`DetectionThreshold/ConfidenceThreshold`瑜?理쒕? `0.12`源뚯? ??떠 ??誘쇨컧?섍쾶 ?뺤씤?쒕떎. ?꾩뿭 ?먮룞 寃異쒖쓽 ?ъ슜??湲곗?媛?`0.2/0.25/0.7`? ?좎??쒕떎.
- ROI ?꾨낫媛 媛源뚯슫 frame??紐곕┛ 寃쎌슦 留??꾨낫留덈떎 seek?섏? ?딄퀬 ??踰덉쓽 sequential read濡??댁뼱 ?쎈룄濡?理쒖쟻?뷀뻽?? ?꾨낫 frame 媛꾧꺽??`12`?꾨젅?꾨낫???щ㈃ ?ㅼ떆 seek??湲?援ш컙??遺덊븘?뷀븯寃??붿퐫?쒗븯吏 ?딅뒗??
- 湲?援ш컙?먯꽌 ROI ?꾨낫 ?곹븳???곸슜?????낅젰 ?쒖꽌 ?명뼢???앷린吏 ?딅룄濡? ?꾨낫瑜?frame index 湲곗??쇰줈 癒쇱? ?뺣젹????`maxCandidates`瑜??곸슜?쒕떎.
- confidence媛 ??퀬 吏??湲몄씠媛 吏㏃? ?⑤컻 track? ?ㅺ?異??꾨낫濡?蹂닿퀬 ?쒓굅?쒕떎.
- confidence媛 ?믩뜑?쇰룄 1~2媛?寃異쒕쭔 媛吏??묒? ?⑤컻 track? 臾쇨굔 ?ㅽ깘 媛?μ꽦???믪쑝誘濡??쒓굅?쒕떎.
- ?묒? track??3媛??댁긽 寃異쒕줈 ?댁뼱吏硫?以묒븰??諛섏そ ?쇨뎬 媛?μ꽦??怨좊젮??利됱떆 ?쒓굅?섏? ?딅뒗??
- ?? ?붾㈃ 媛?μ옄由ъ뿉 ?우? ?묒? ?⑤컻 ?꾨낫??諛섏そ ?쇨뎬 媛?μ꽦???덉쑝誘濡?蹂댁〈?쒕떎.
- ?섎룞 mask媛 ?덈뒗 frame? ?먮룞 track 蹂댁젙 ??곸뿉???쒖쇅?쒕떎.
- 湲곗〈 `WorkspaceViewModel.ApplyAutoTemporalFixes()`??track ?꾩쿂由??몄텧濡?異뺤냼?덈떎.
- smoke harness???먮룞 寃異???`FaceTrackInterpolator`瑜??곸슜?섍퀬 `[SmokeFaceTrackPost]` 濡쒓렇瑜??④린?꾨줉 留욎톬??
- synthetic 寃利??ㅽ겕由쏀듃?먯꽌 吏㏃? gap fill, ?뺤젙 track lost-fill, low-confidence ?⑤컻 ?쒓굅, 1~2媛?寃異쒖쭨由??묒? ?ㅽ깘 ?쒓굅, ?쒓굅??track??蹂닿컙 ?ъ깮??李⑤떒, 3?꾨젅???댁긽 以묒븰 諛섏そ ?쇨뎬 ?꾨낫 蹂댁〈??吏곸젒 ?뺤씤?섎룄濡??덈떎.
- smoke harness??`FaceTrackRoiRefiner`瑜??몄텧??ROI refiner 寃쎈줈瑜?console gate?먯꽌 寃利앺븳??

寃利?

- `dotnet build FaceShield.sln` ?깃났. 湲곗〈 FFmpeg.AutoGen obsolete warning 7媛쒕쭔 諛쒖깮?덈떎.
- `.tmp/srcTest-smoke/smoke-0600-3s.mp4` ?⑥씪 optimized CPU all-frame 寃利앹뿉??`[SmokeFaceTrackPost] tracks=3, filled=0, removedShort=0, rewritten=8`???뺤씤?덈떎.
- `scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾???듦낵?덈떎.
- ?덉쭏 gate??baseline怨?optimized 紐⑤몢 track ?꾩쿂由??곸슜 ??`baselineFrames=8`, `optimizedFrames=8`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- auto tune 吏㏃? 寃利앹? `FaceOnnxDetector/GPU:DirectML`, `mode=pipe-parallel`, `detects=150`, `interpolated=0`?쇰줈 ?듦낵?덇퀬, ?꾩쿂由?濡쒓렇??`tracks=3`, `filled=0`, `removedShort=0`, `rewritten=8`?댁뿀??
- `scripts/verify-face-track-postprocess.ps1` ?ㅽ뻾 寃곌낵 `tracks=6`, `filled=1`, `gapFrames=11`, `lostFilled=3`, `lostFrames=33,34,35`, `removedShort=3`, `rewritten=13`, `filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52`瑜??뺤씤?덈떎. frame 11? gap-fill ROI ?꾨낫, frame 25???붾㈃ 媛?μ옄由?諛섏そ ?쇨뎬 ?꾨낫 蹂댁〈 耳?댁뒪, frame 30~35???뺤젙 track lost-fill 耳?댁뒪, frame 50~52??以묒븰?먯꽌 3?꾨젅???댁긽 ?댁뼱吏???묒? 諛섏そ ?쇨뎬 ?꾨낫 蹂댁〈 耳?댁뒪??
- `scripts/verify-auto-mosaic-default.ps1` ?듯빀 寃利앹뿉??ROI refiner媛 誘쇨컧??ROI ?꾩슜 detector濡?`attempts=8`, `hits=0` ?ㅽ뻾?먭퀬, baseline/optimized 紐⑤몢 媛숈? 寃곌낵瑜??좎??덈떎. ???섑뵆?먯꽌??ROI crop??異붽? ?쇨뎬??李얠???紐삵뻽吏留? gap-fill 5?꾨젅?꾧낵 lost-fill 3?꾨젅?꾩쓣 紐⑤몢 ?ш?異???곸쑝濡??뺤씤?섎㈃???덉쭏 gate瑜?源⑥? ?딅뒗 寃껋? ?뺤씤?덈떎.
- ROI hit ???援ш컙 `.tmp/srcTest-smoke/smoke-0900-2s.mp4`瑜?`scripts/verify-auto-mosaic-default.ps1`??異붽??덈떎. ??gate??`FaceTrackRoiRefiner`媛 ?ㅼ젣 援ш컙?먯꽌 `attempts=11`, `hits=5`瑜??대뒗吏 ?뺤씤?쒕떎. ROI seek 理쒖쟻?????⑤룆 ?ㅽ뻾?먯꽌??`seeks=4`, `decoded=26`, `elapsedMs=9,455`???
- ?좏깮??export smoke gate `-RunExportSmoke`瑜?`scripts/verify-auto-mosaic-default.ps1`??異붽??덈떎. `.tmp/srcTest-smoke/smoke-1200-2s.mp4`?먯꽌 ?먮룞 寃異???export源뚯? ?ㅽ뻾?섍퀬 `[ExportRunSummary]`??`bitmapMaskFrames=0`, `directFaceFrames>0`, output ?앹꽦 濡쒓렇瑜??뺤씤?쒕떎.
- `.tmp/srcTest-smoke/smoke-1200-30s.mp4` 30珥?以묎컙 湲몄씠 寃利앹뿉??`processed=899`, `detects=899`, `interpolated=0`, `totalMs=357,398ms`, `regular=614`, `small=2037`, `rejected=2111`, `statsRejected=131`???뺤씤?덈떎. track ?꾩쿂由щ뒗 `tracks=244`, `filled=431`, `lostFilled=104`, `removedShort=77`, `rewritten=778`?댁뿀怨? ROI refiner???곹븳 32媛??꾨낫?먯꽌 `attempts=32`, `hits=22`, `seeks=4`, `decoded=77`, `elapsedMs=14,599`???
- ??30珥?寃利앹쓣 ?ы쁽?????덈룄濡?`scripts/verify-auto-mosaic-default.ps1`???좏깮??`-RunMediumAuto` gate瑜?異붽??덈떎. ??gate??`processed=899`, `detects=899`, `interpolated=0`, track 蹂댁젙 諛쒖깮, ROI `attempts=32`, ROI `hits>0`???뺤씤?쒕떎.

?⑥? ?쒓퀎:

- ?ㅼ젣 ?곸긽 smoke 援ш컙?먯꽌??gap 蹂닿컙怨??⑤컻 ?쒓굅媛 諛쒖깮?섏? ?딆븯?? synthetic 寃利앹뿉?쒕뒗 湲곕뒫 ?먯껜瑜??뺤씤?덉?留? ?ㅼ젣 臾몄젣 援ш컙?먯꽌 媛쒖꽑 ?섏튂???꾩쭅 ?뺤씤?섏? 紐삵뻽??
- ?묒? ?쇨뎬 誘명깘 ?먯껜瑜?以꾩씠??detector/backend???꾩쭅 援ы쁽?섏? ?딆븯?? ROI refiner??湲곕낯 寃쎈줈??gap-fill/lost-fill ?꾨낫源뚯? ?뺤옣?덉?留? ?꾩옱 湲곕낯 ?섑뵆?먯꽌??`hits=0`?대씪 ?ㅼ젣 誘명깘 媛먯냼 ?④낵???꾩쭅 ?뺤씤?섏? ?딆븯??
- 湲?export 蹂묐ぉ 媛먯냼???대쾲 蹂寃쎌쓽 ??곸씠 ?꾨땲硫? ?ㅼ젣 湲?援ш컙 `ExportRunSummary` 湲곗??쇰줈 怨꾩냽 痢≪젙?댁빞 ?쒕떎.

## 2026-05-12 ?묒? ?쇨뎬 ?꾪꽣 蹂닿컯
?묒? ?쇨뎬 誘명깘 媛?μ꽦??以꾩씠湲??꾪빐 `AutoMaskGenerator`??face size filter瑜?蹂댁닔?곸쑝濡??꾪솕?덈떎.

李멸퀬 湲곗?:

- ?ъ슜?먭? 2026-05-11 吏??뚯뒪?몄뿉???ъ슜??detector threshold??`DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`?대떎.
- ??媛믩뱾? `HomePageViewModel.BuildDetectorOptions()`?먯꽌 `FaceOnnxDetectorOptions`濡??꾨떖?섍퀬, `DetectorAutoTuner.CloneOptions()`??threshold 媛믪쓣 ?좎??쒕떎.
- ?대쾲 蹂寃쎌? ??detector threshold瑜?吏곸젒 諛붽씀吏 ?딄퀬, detector媛 諛섑솚???꾨낫瑜?`AutoMaskGenerator` ?꾪꽣? `FaceTrackInterpolator` ?꾩쿂由ъ뿉???대뼸寃??대━嫄곕굹 ?쒓굅?좎? 議곗젙??寃껋씠??

湲곗〈 ?쒓퀎:

- 湲곗〈 `MinFaceAreaRatio=0.00075`??3840x2160 ?곸긽 湲곗? ??6,220px, ?뺤궗媛곹삎 ?섏궛 ??79x79px 誘몃쭔 ?쇨뎬 ?꾨낫瑜?踰꾨┫ ???덈떎.
- ?⑥닚??硫댁쟻 湲곗?留???텛硫?臾쇨굔 ?ㅺ?異쒖씠 ?섏뼱?????덈떎.

蹂寃??댁슜:

- ?쇰컲 face ?꾨낫 湲곗?? ?좎??쒕떎.
- ?묒? face ?꾨낫??`MinSmallFaceAreaRatio=0.00025` ?댁긽?대㈃??confidence媛 `0.72` ?댁긽???뚮쭔 ?대┛??
- ?묒? face ?꾨낫??confidence媛 ?믪븘??skin/edge/luma variance 湲곕컲 ?쎌? ?듦퀎 寃?щ? 媛뺤젣濡??듦낵?댁빞 ?쒕떎.
- ?쇰컲 ?꾨낫??湲곗〈 high-confidence stats bypass 寃쎈줈???좎??????쇰컲 ?쇨뎬 泥섎━ 鍮꾩슜怨?湲곗〈 寃곌낵 蹂?붾? 以꾩???
- `[AutoRunSummary]`??`roi=` ?붿빟??`regular`, `small`, `rejected`, `statsRejected` filter ?듦퀎瑜??④린?꾨줉 ?덈떎.

寃利?

- `dotnet build FaceShield.sln` ?깃났. 湲곗〈 FFmpeg.AutoGen obsolete warning 7媛쒕쭔 諛쒖깮?덈떎.
- `scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾???듦낵?덈떎.
- ?덉쭏 gate??`baselineFrames=8`, `optimizedFrames=8`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- auto tune 吏㏃? 寃利앹? `FaceOnnxDetector/GPU:DirectML`, `mode=pipe-parallel`, `detects=150`, `interpolated=0`?쇰줈 ?듦낵?덈떎.
- 怨꾩륫 異붽? ??`.tmp/srcTest-smoke/smoke-0600-3s.mp4` ?⑥씪 optimized CPU all-frame 寃利앹뿉??`filter=regular=8, small=0, rejected=0, statsRejected=0` 濡쒓렇瑜??뺤씤?덈떎.
- `.tmp/srcTest-smoke/smoke-0900-2s.mp4` ?⑥씪 optimized CPU all-frame 寃利앹뿉??`filter=regular=84, small=0, rejected=0, statsRejected=0`, track ?꾩쿂由?`tracks=5`, `filled=5`, `removedShort=0`, `rewritten=51`???뺤씤?덈떎. ?ㅼ젣 ?곸긽 援ш컙?먯꽌 gap 蹂닿컙??諛쒖깮??????꾨낫濡??붾떎.
- `.tmp/srcTest-smoke/smoke-1200-2s.mp4` ?⑥씪 optimized CPU all-frame 寃利앹뿉??`filter=regular=28, small=0, rejected=12, statsRejected=0`, track ?꾩쿂由?`tracks=1`, `filled=0`, `removedShort=0`, `rewritten=28`???뺤씤?덈떎.
- `.tmp/srcTest-smoke/smoke-1500-2s.mp4` ?⑥씪 optimized CPU all-frame 寃利앹뿉??`filter=regular=0, small=2, rejected=8, statsRejected=3`, track ?꾩쿂由?`tracks=2`, `filled=0`, `removedShort=0`, `rewritten=2`瑜??뺤씤?덈떎. ?묒? ?쇨뎬 ?꾨낫媛 ?ㅼ젣濡??좎??섎뒗 ????꾨낫濡??붾떎.
- ?댄썑 媛숈? 援ш컙??`DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, `-DumpDetections`濡??ш?利앺뻽?? ?묒? ?꾨낫??frame 2/3/4??鍮④컙 ?섍굔/臾쇨굔 ?ㅽ깘?쇰줈 ?뺤씤?섏뼱 ?묒? ?⑤컻 track ?쒓굅 湲곗???異붽??덈떎.
- ?쒓굅???묒? track? gap 蹂닿컙?먯꽌 ?ㅼ떆 ?댁븘?섏? ?딅룄濡?蹂댁젙?덈떎. ?ъ떎??寃곌낵 `filter=regular=0, small=5, rejected=13, statsRejected=4`, `tracks=4`, `filled=0`, `removedShort=4`, `rewritten=1`, `faceMaskFrames=1`?댁뿀??
- 以묒븰??鍮④컙 ?섍굔/臾쇨굔 ?ㅽ깘? ?쒓굅?먭퀬, ?⑥? 1媛쒕뒗 frame 56???붾㈃ ?ㅻⅨ履?媛?μ옄由??묒? ?꾨낫?? ?꾨젅???대?吏濡??≪븞 ?뺤씤??寃곌낵 ?붾㈃ ?앹뿉 嫄몃┛ 諛섏そ ?쇨뎬 ?꾨낫濡?蹂댁뿬 ?꾩옱 ?뺤콉?濡?蹂댁〈?쒕떎. ?댄썑 ?ㅻⅨ 援ш컙?먯꽌 媛?μ옄由?臾쇱껜 ?ㅽ깘??諛섎났?섎㈃ 媛?μ옄由??꾨낫??verifier ?먮뒗 ???꾧꺽??edge partial-face 寃利앹씠 ?꾩슂?섎떎.
- ?묒? track ?쒓굅 湲곗?? `1~2媛?寃異?濡??쒗븳?덈떎. ?щ엺???ㅻ룎硫댁꽌 ?쇨뎬??諛섎쭔 蹂댁씠??寃쎌슦泥섎읆 以묒븰?먯꽌 3?꾨젅???댁긽 ?댁뼱吏???묒? ?꾨낫??`Tentative`濡??④꺼 ?댄썑 lifecycle/ROI ?ш?利???곸씠 ?섍쾶 ?쒕떎.

?⑥? ?쒓퀎:

- ??蹂寃쎌? detector媛 ?대? 諛섑솚???묒? ?쇨뎬 ?꾨낫瑜???踰꾨━??蹂닿컯?대떎. detector ?먯껜媛 ?꾨낫瑜?諛섑솚?섏? 紐삵븯???묒? ?쇨뎬? ROI ?ш?異? verifier/refiner, ?먮뒗 ??detector backend媛 ?꾩슂?섎떎.
- ?묒? ?쇨뎬 ?꾨낫媛 ?좎??섎뒗 ???援ш컙? ?뺤씤?덉?留? ?ㅼ젣 ?≪븞 湲곗? 誘명깘 frame ??媛먯냼???꾩쭅 ?뺤씤?섏? 紐삵뻽??
- 媛?μ옄由??묒? ?꾨낫??諛섏そ ?쇨뎬 蹂댄샇瑜??꾪빐 蹂댁닔?곸쑝濡??대┛?? ???뺤콉? 媛?μ옄由?臾쇱껜 ?ㅽ깘???④만 ???덉쑝誘濡??ㅼ젣 ?곸긽 ?뺤씤 寃곌낵???곕씪 蹂꾨룄 verifier媛 ?꾩슂?섎떎.

## 2026-05-12 auto tune provider ?좏깮 蹂닿컯
threshold瑜?`DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`濡???텣 ?? 吏㏃? auto tune ?섑뵆?먯꽌 GPU媛 ?좏깮?먯?留??ㅼ젣 5珥?寃異?wall-clock??CPU 蹂묐젹 寃쎈줈蹂대떎 ?먮젮吏??耳?댁뒪瑜??뺤씤?덈떎.

蹂寃??댁슜:

- `DetectorAutoTuner`??provider ?꾨낫 痢≪젙???⑥씪 ?꾨젅?꾩씠 ?꾨땲??理쒕? 3媛??곗냽 ?꾨젅??湲곗??쇰줈 諛붽엥??
- GPU??quality gate瑜??듦낵?섎뜑?쇰룄 CPU 理쒓퀬 ?꾨낫蹂대떎 異⑸텇??鍮좊? ?뚮쭔 ?좏깮?섎룄濡?`GpuPreferenceMinScoreRatio`瑜?`1.20`?쇰줈 議곗젙?덈떎.
- CPU/GPU ?꾨낫 ?먯닔瑜?遺꾨━??怨꾩궛?쒕떎. ?댁쟾 援ъ“?먯꽌??GPU媛 ?쇰떒 ?꾩껜 理쒓퀬?먯씠 ?섎㈃ CPU ?鍮??밴꺽 margin???ъ떎???고쉶?????덉뿀??
- CPU ?꾨낫??怨좎젙 thread ?섎퓧 ?꾨땲??湲곕낯 ORT thread ?ㅼ젙 ?꾨낫(`CPU <n>?몄뀡/default`)瑜?異붽??덈떎. ?섎룞 smoke??湲곕낯 CPU 寃쎈줈? 媛숈? ?꾨낫瑜?auto-tune?먯꽌??鍮꾧탳?섍린 ?꾪븿?대떎.
- `scripts/verify-auto-mosaic-default.ps1`?????댁긽 GPU ?좏깮??怨좎젙 ?붽뎄?섏? ?딅뒗?? CPU/GPU 以?auto tune???좏깮??provider媛 `FaceOnnxDetector/CPU` ?먮뒗 `FaceOnnxDetector/GPU:DirectML`濡??뺤긽 ?숈옉?섍퀬, `pipe-parallel`, ???꾨젅??寃異? `interpolated=0` 議곌굔??留뚯”?섎뒗吏 ?뺤씤?쒕떎.
- `scripts/verify-auto-mosaic-default.ps1`媛 `scripts/verify-face-track-postprocess.ps1`瑜?癒쇱? ?ㅽ뻾?섎룄濡?臾띠뿀?? 湲곕낯 寃利???踰덉쑝濡?track gap 蹂닿컙, ?묒? ?ㅽ깘 ?쒓굅, 諛섏そ ?쇨뎬 ?꾨낫 蹂댁〈 ?뺤콉源뚯? 媛숈씠 ?뺤씤?쒕떎.
- `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport`瑜?異붽??덈떎. 30珥????援ш컙?먯꽌 ?먮룞 寃異???export源뚯? ?섑뻾?섍퀬 `processed=899`, ROI hit, `ExportRunSummary.frames=902`, `bitmapMaskFrames=0`, `directFaceFrames>0`, output ?앹꽦??assertion?쒕떎.
- `scripts/run-srcTest-smoke.ps1`??留??ㅽ뻾留덈떎 怨좎쑀 harness ?대뜑瑜??ъ슜?쒕떎. ?댁쟾 smoke ?꾨줈?몄뒪媛 ?⑥븘 ?덉뼱??怨좎젙 `SmokeHarness.exe` ?뚯씪 ?좉툑 ?뚮Ц???ㅼ쓬 寃利앹씠 ?ㅽ뙣?섎뒗 ?곹솴??以꾩씤??

寃利?

- `dotnet build FaceShield.sln` ?깃났. 湲곗〈 FFmpeg.AutoGen obsolete warning 7媛쒕쭔 諛쒖깮?덈떎.
- `git diff --check` ?듦낵.
- `.tmp/srcTest-smoke/smoke-0600-5s.mp4`?먯꽌 `-UseAutoTune` ?ъ떎??寃곌낵, tuner媛 `CPU 2?몄뀡/4?ㅻ젅??瑜??좏깮?덈떎.
- 媛숈? 寃利앹뿉??`FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=16`, `totalMs=80,877ms`瑜??뺤씤?덈떎.
- 吏곸쟾 GPU ?좏깮 寃쎈줈??媛숈? 5珥?寃利앹? `FaceOnnxDetector/GPU:DirectML`, `totalMs=107,471ms`??쇰?濡? ???섑뵆?먯꽌???먮┛ GPU 怨좎젙 ?좏깮??以꾩???
- ?댄썑 `scripts/verify-auto-mosaic-default.ps1` ?꾩껜 湲곕낯 寃利앹씠 ?ㅼ떆 ?듦낵?덈떎. 媛숈? 5珥?寃利앹뿉??理쒖떊 ?ㅽ뻾? `FaceOnnxDetector/GPU:DirectML`, `totalMs=59,534ms`濡??듦낵?덈떎. provider ?좏깮? ?ㅽ뻾 ?쒖젏???λ퉬 遺?섏뿉 ?곕씪 CPU/GPU媛 ?щ씪吏????덉쑝誘濡? gate??provider 怨좎젙???꾨땲???덉쭏 ?좎?? ???꾨젅??蹂묐젹 寃쎈줈 吏꾩엯???뺤씤?쒕떎.
- 怨좎쑀 harness ?대뜑 蹂寃???`.tmp/srcTest-smoke/smoke-1500-2s.mp4` 吏㏃? smoke???듦낵?덈떎. 寃곌낵??`FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `processed=59`, `faceMaskFrames=1`, `removedShort=4`, `totalMs=21,827ms`???
- ?꾩쿂由??뺤콉 gate瑜??듯빀????`scripts/verify-auto-mosaic-default.ps1`瑜??ㅼ떆 ?ㅽ뻾?덇퀬 ?꾩껜 ?듦낵?덈떎. 理쒖떊 ?듯빀 ?ㅽ뻾?먯꽌 `track-postprocess-policy`??`filled=1`, `lostFilled=3`, `lostFrames=33,34,35`, `removedShort=3`, `filledFrames=10,11,12,25,30,31,32,33,34,35,50,51,52`??? ?덉쭏 gate??lost-fill ?곸슜 ?꾩뿉??`baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`?쇰줈 ?듦낵?덇퀬, CPU 蹂묐젹 寃쎈줈??`totalMs=34,381ms`濡?CPU single baseline `61,097ms`蹂대떎 鍮⑤옄?? ?덉쭏 gate???ㅼ젣 lost-fill frame??baseline/optimized 紐⑤몢 `lostFrames=6,7,8`濡??쇱튂?덈떎. auto tune gate??`FaceOnnxDetector/GPU:DirectML`, `processed=150`, `interpolated=0`, `lostFilled=3`, `lostFrames=6,7,8`, `totalMs=64,020ms`???
- `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke -RunMediumAuto -RunLongAutoTune`媛 ?듦낵?덈떎. ?대떦 ?ㅽ뻾?먯꽌 export smoke??`bitmapMaskFrames=0`, `directFaceFrames=31`, `totalMs=12,299ms`?怨? 30珥?medium CPU gate??`processed=899`, `detects=899`, `filled=431`, `lostFilled=104`, `removedShort=77`, ROI `attempts=32`, `hits=22`, `totalMs=397,825ms`???
- 媛숈? verifier ?ㅽ뻾?먯꽌 short auto-tune? `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=84,769ms`?怨? long auto-tune? `FaceOnnxDetector/GPU:DirectML`, `processed=899`, `detects=899`, `interpolated=0`, ROI `attempts=32`, `hits=22`, `totalMs=430,952ms`???
- GPU ?밴꺽 margin怨?CPU default ?꾨낫 異붽? ??`.tmp/srcTest-smoke/smoke-0600-5s.mp4 -UseAutoTune`? ?ㅼ떆 `FaceOnnxDetector/GPU:DirectML`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=60,447ms`濡??꾨즺?먮떎. 媛숈? 蹂寃?吏곹썑 30珥?long auto-tune? `CPU 2?몄뀡/4?ㅻ젅??瑜??좏깮?덇퀬 `processed=899`, `detects=899`, `interpolated=0`, ROI `attempts=32`, `hits=22`, `totalMs=438,618ms`??? provider ?좏깮? 遺?섏뿉 ?곕씪 ?붾뱾由????덉뼱 gate??provider 怨좎젙蹂대떎 ???꾨젅??泥섎━/?덉쭏/蹂묐젹 寃쎈줈瑜??뺤씤?쒕떎.
- 30珥????援ш컙 export ?ы븿 smoke???ㅽ뻾?덈떎. `.tmp/srcTest-smoke/smoke-1200-30s.mp4`?먯꽌 ?먮룞 寃異쒖? `processed=899`, `detectMs=762,418`, `totalMs=382,985ms`, track ?꾩쿂由щ뒗 `filled=431`, `lostFilled=104`, `removedShort=77`, ROI??`attempts=32`, `hits=22`, `elapsedMs=17,773`?댁뿀?? ?댁뼱吏?export??`frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=778`, `swsToBgraMs=15,927`, `maskMs=47,715`, `swsToEncMs=24,851`, `encodeMs=4,361`, `totalMs=148,317ms`??? ??援ш컙?먯꽌??export蹂대떎 detector媛 ????蹂묐ぉ?대떎.
- `-RunMediumExport` 異붽? ??`scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾???ㅼ떆 ?듦낵?덈떎. 理쒖떊 ?ㅽ뻾?먯꽌 ?덉쭏 gate??`baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, CPU 蹂묐젹 `totalMs=34,870ms`?怨? ROI-hit ???gate??`attempts=11`, `hits=5`, `elapsedMs=9,134`??? auto-tune? `CPU 2?몄뀡/default`, `provider=CPU`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=52,773ms`濡??듦낵?덈떎.
- `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport`???ㅼ젣 ?듦낵?덈떎. ???ㅽ뻾?먯꽌 ?덉쭏 gate??CPU single `totalMs=54,409ms`, CPU 蹂묐젹 `totalMs=34,181ms`, `avgBestIou=1.000`?댁뿀?? ROI-hit ???gate??`attempts=11`, `hits=5`, `elapsedMs=9,288`?댁뿀?? `medium-auto-export`???먮룞 寃異?`processed=899`, `detectMs=629,598`, `totalMs=316,366ms`, track `filled=431`, `lostFilled=104`, `removedShort=77`, ROI `attempts=32`, `hits=22`, `elapsedMs=13,718`???뺤씤?덇퀬, export??`frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=778`, `maskMs=39,891`, `swsToEncMs=22,251`, `encodeMs=4,160`, `totalMs=127,750ms`濡??듦낵?덈떎. 留덉?留?short auto-tune gate??`CPU 2?몄뀡/default`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=53,885ms`???
- `MaskedVideoExporter.ApplyFaceRectsAndBlur()`???⑥씪 ?쇨뎬 fast path瑜?異붽??덈떎. ?쇨뎬??1媛쒖씤 frame? 湲곗〈 ellipse alpha/soft edge 怨꾩궛? ?좎??섎릺, per-pixel shape list ?쒗쉶? radius map ?앹꽦/議고쉶 鍮꾩슜??嫄대꼫?대떎. ?곸슜 ??`scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke`媛 ?ㅼ떆 ?듦낵?덈떎. ???ㅽ뻾?먯꽌 direct face export smoke??`frames=61`, `bitmapMaskFrames=0`, `directFaceFrames=31`, `maskMs=713`, `swsToEncMs=1,089`, `encodeMs=479`, `totalMs=7,066ms`?怨? short auto-tune gate??`CPU 2?몄뀡/default`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=61,999ms`???
- ?꾩껜 ?먮낯 `srcTest/260102_jp_10.mp4`??`3840x2160`, `30000/1001fps`, `duration=1067.599867`, `nb_frames=31996`, ?뚯씪 ?ш린 ??`2.3GB`?? 30珥????援ш컙 ?먮룞 寃異?export ?섏튂瑜??⑥닚 ?섏궛?섎㈃ ?꾩껜 ?먮낯 ??곗? 紐??쒓컙 ?⑥쐞媛 ?????덈떎.
- detector ?몄텧 ?섎? 以꾩씠??`sparse-pipe-parallel`???덉쭏 gate?먯꽌 ?뺤씤?덈떎. `.tmp/srcTest-smoke/smoke-0600-3s.mp4`??`-OptimizedDetectEvery 2`瑜??곸슜?섎㈃ `detects=45`, `interpolated=9`, `detectMs=32,705`, `totalMs=16,820ms`濡?留ㅼ슦 鍮⑤씪議뚯?留? FaceONNX all-frame baseline ?鍮?`baselineFrames=19`, `optimizedFrames=22`, `onlyBaseline=3`, `onlyOptimized=6`, `avgBestIou=0.930`, `minBestIou=0.627`, `passed=False`??? ?곕씪??`DetectEveryNFrames=2` sparse tracking? ?꾩옱 ?덉쭏 理쒖슦??湲곕낯媛믪쑝濡??밴꺽?섏? ?딅뒗??

?⑥? ?쒓퀎:

- auto tune 痢≪젙 ?꾨젅???섏? ?꾨낫 ?섎? ?섎졇湲??뚮Ц???먮룞 ?쒖옉 ???쒕떇 ?쒓컙??議곌툑 ?????덈떎.
- ?κ린 ?곸긽?먯꽌 CPU/GPU ?곗쐞媛 ?ㅽ뻾 ?쒖젏 遺?섏뿉 ?곕씪 諛붾뚮?濡? ?꾩껜 ?곸긽 湲곗? 理쒖쥌 ?좏깮 ?뺤콉? ??湲????援ш컙?쇰줈 怨꾩냽 寃利앺빐???쒕떎.

## 2026-05-12 completion audit
紐⑺몴瑜?援ъ껜 deliverable濡??섎늻硫??ㅼ쓬怨?媛숇떎.

| ?붽뎄/?꾨즺 湲곗? | ?꾩옱 利앷굅 | ?먯젙 |
| --- | --- | --- |
| 湲곕낯 ?덉쭏???ъ깮?섏? ?딅뒗 ?먮룞 紐⑥옄?댄겕 | `DownscaleRatio=1.0`, `DetectEveryNFrames=1` 寃쎈줈瑜??좎??쒕떎. `scripts/verify-auto-mosaic-default.ps1` ?덉쭏 gate?먯꽌 baseline/optimized 紐⑤몢 `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`?쇰줈 ?듦낵?덈떎. | 異⑹” |
| 泥섎━ ?띾룄 媛쒖꽑 | 媛숈? 6遺?3珥??대┰ gate?먯꽌 CPU single baseline `totalMs=66,771ms`, CPU 蹂묐젹 `totalMs=36,138ms`瑜??뺤씤?덈떎. 理쒖떊 媛뺥븳 verifier?먯꽌 3珥??덉쭏 gate??CPU single `totalMs=70,544ms`, CPU 蹂묐젹 `totalMs=41,973ms`濡??듦낵?덇퀬, 30珥?medium CPU gate??`processed=899`, `totalMs=397,825ms`??? auto tune? CPU/GPU ?꾨낫瑜?紐⑤몢 鍮꾧탳?섍퀬 `pipe-parallel` 寃쎈줈濡??듦낵?쒕떎. `DetectEveryNFrames=2` sparse??`totalMs=16,820ms`源뚯? 以꾩뿀吏留??덉쭏 gate ?ㅽ뙣濡?湲곕낯 ?밴꺽?섏? ?딅뒗?? | 遺遺?異⑹” |
| ?묒? ?쇨뎬 ?꾨낫 蹂댁〈 | `MinSmallFaceAreaRatio=0.00025`, `SmallFaceConfidenceMin=0.72`, stats gate瑜?異붽??덇퀬 15遺?2珥?援ш컙?먯꽌 ?묒? ?꾨낫 ?좎?/?ㅽ깘 ?쒓굅瑜??뺤씤?덈떎. | 遺遺?異⑹” |
| ?묒? ?쇨뎬 detector 誘명깘 ?먯껜 媛먯냼 | ?꾩옱 蹂寃쎌? detector媛 諛섑솚???꾨낫瑜???踰꾨━??諛⑹떇?대떎. detector媛 ?꾨낫瑜?諛섑솚?섏? 紐삵븳 ?쇨뎬???덈줈 李얜뒗 湲곕낯 `FaceTrackRoiRefiner` 寃쎈줈??gap-fill/lost-fill ?꾨낫源뚯? 異붽??덇퀬 ROI ?꾩슜 threshold????誘쇨컧?섍쾶 ??톬?? 9遺?2珥????援ш컙?먯꽌??`attempts=11`, `hits=5`濡??ㅼ젣 ROI 蹂댁젙 hit瑜??뺤씤?덈떎. ?ㅻ쭔 ?묒? ?쇨뎬 ?꾩슜 ??detector backend???녿떎. | 遺遺?異⑹” |
| 臾쇨굔 ?ㅽ깘 媛먯냼 | 15遺?2珥?援ш컙?먯꽌 鍮④컙 ?섍굔/臾쇨굔 ?ㅽ깘? `removedShort=4`濡??쒓굅?먭퀬, synthetic gate?먯꽌 1~2媛?寃異쒖쭨由??묒? ?ㅽ깘 ?쒓굅? 蹂닿컙 ?ъ깮??李⑤떒???뺤씤?덈떎. | 遺遺?異⑹” |
| 諛섏そ ?쇨뎬/?ㅻ룄???쇨뎬 蹂댁〈 | synthetic gate?먯꽌 媛?μ옄由?諛섏そ ?쇨뎬 ?꾨낫? 以묒븰 3?꾨젅???묒? ?꾨낫 蹂댁〈???뺤씤?덈떎. 15遺?2珥?frame 56???ㅻⅨ履?媛?μ옄由??꾨낫???꾨젅???대?吏濡??뺤씤??蹂댁〈?덈떎. | 遺遺?異⑹” |
| ?뺤젙 track ?좎? | ?뺤젙 track lost-fill??異붽??덇퀬 synthetic gate?먯꽌 `lostFilled=3`, `lostFrames=33,34,35`, ?ㅼ젣 9遺?2珥?smoke?먯꽌 `lostFilled=10`???뺤씤?덈떎. | 遺遺?異⑹” |
| ?ㅽ깘 ?붿긽 諛⑹? | lost-fill? 3媛??댁긽 寃異쒕맂 媛뺥븳 track留? 理쒕? 3?꾨젅?꾨쭔 ?곸슜?쒕떎. ?묒? track? lost-fill ??곸뿉???쒖쇅?쒕떎. | 遺遺?異⑹” |
| detector/backend 援먯껜 媛?μ꽦 | `FaceDetectorBackend.ScrfdOnnx`, `ScrfdOnnxDetectorOptions`, `ScrfdOnnxDetector`瑜?異붽??덇퀬 `FaceDetectorFactory`?먯꽌 ?앹꽦 媛?ν븯?? `scripts/run-srcTest-smoke.ps1 -ScrfdModelPath <model.onnx>`濡?FaceONNX baseline ?鍮?SCRFD optimized A/B瑜??ㅽ뻾?????덈떎. `.tmp`??諛쏆? SCRFD 500M/10G ?꾨낫???ㅽ뻾?먯?留?quality gate瑜??듦낵?섏? 紐삵빐 湲곕낯 ?밴꺽?섏? ?딅뒗?? | 遺遺?異⑹” |
| ROI ?ш?異?2李?verifier | `FaceTrackRoiRefiner`媛 track gap-fill/lost-fill ?꾨낫留?raw BGRA濡??ㅼ떆 ?쎌뼱 ROI crop ?ш?異쒖쓣 ?섑뻾?쒕떎. ?꾩뿭 threshold???좎??섍퀬 ROI ?꾩슜 CPU detector留?`0.12/0.12`濡???誘쇨컧?섍쾶 ?뚮┛?? 湲곕낯 ?덉쭏 gate?먯꽌??`attempts=8`, `hits=0`?쇰줈 ?덉쭏 ?좎?媛 ?뺤씤?먭퀬, 9遺?2珥?ROI-hit ???gate?먯꽌??`attempts=11`, `hits=5`濡??ㅼ젣 蹂댁젙 hit媛 ?뺤씤?먮떎. ?꾨낫 frame ?뺣젹/sequential read 理쒖쟻?????대떦 援ш컙? `seeks=4`, `decoded=26`, `elapsedMs=9,455`濡?怨꾩륫?먮떎. 媛뺥븳 2李?紐⑤뜽 verifier???꾩쭅 ?녿떎. | 遺遺?異⑹” |
| export 蹂묐ぉ 媛쒖꽑 | direct face rect export? summary???좎??쒕떎. ?⑥씪 ?쇨뎬 direct blur fast path 異붽? ??理쒖떊 `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke`?먯꽌 `.tmp/srcTest-smoke/smoke-1200-2s.mp4` export媛 `bitmapMaskFrames=0`, `directFaceFrames=31`, `swsToBgraMs=552`, `maskMs=713`, `swsToEncMs=1,089`, `encodeMs=479`, `totalMs=7,066ms`濡??꾨즺?먮떎. `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport`濡??ы쁽 媛?ν븳 30珥????援ш컙 export gate???듦낵?덇퀬, `frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=778`, `maskMs=39,891`, `swsToEncMs=22,251`, `encodeMs=4,160`, `totalMs=127,750ms`??? ?꾩껜 17遺??먮낯 export 蹂묐ぉ? ?꾩쭅 ?녿떎. | 遺遺?異⑹” |
| ?ㅼ젣 `srcTest` ???援ш컙 寃利?| ?먮낯 `srcTest/260102_jp_10.mp4`??`3840x2160`, `duration=1067.599867`, `nb_frames=31996`濡??뺤씤?덈떎. 6遺?9遺?12遺?15遺?吏㏃? clip smoke? 12遺?30珥??먮룞 寃異?smoke媛 ?덈떎. 理쒖떊 30珥?寃利앹뿉?쒕뒗 `processed=899`, `detects=899`, `filled=431`, `lostFilled=104`, `removedShort=77`, ROI `attempts=32`, `hits=22`, CPU medium `totalMs=397,825ms`, long auto-tune `totalMs=430,952ms` 諛?蹂寃???long auto-tune 吏곸젒 smoke `totalMs=438,618ms`瑜??뺤씤?덈떎. 30珥?export ?ы븿 smoke???먮룞 寃異?`totalMs=382,985ms`, export `totalMs=148,317ms`濡??꾨즺?먮떎. ?꾩껜 17遺??먮낯 end-to-end ?먮룞 寃異?+ export ??곗? ?꾩쭅 ?녿떎. | 遺遺?異⑹” |
| GUI smoke | shell harness 寃利앹? ?듦낵?덈떎. Avalonia GUI?먯꽌 open, preview, auto detect, manual edit, export ?꾩껜 ?먮쫫? 吏곸젒 ?뺤씤?섏? ?딆븯?? | 誘몄셿猷?|
| 鍮뚮뱶/?뺤쟻 gate | `dotnet build FaceShield.sln` ?깃났, `git diff --check` ?듦낵, `scripts/verify-auto-mosaic-default.ps1 -RunExportSmoke -RunMediumAuto -RunLongAutoTune` ?듦낵, `scripts/verify-auto-mosaic-default.ps1 -RunMediumExport` ?듦낵. 理쒖떊 媛뺥븳 verifier?먮뒗 track policy, ?덉쭏 gate, ROI-hit ???gate, direct face export smoke, medium 30珥?gate, medium 30珥?export gate, short auto-tune gate, long auto-tune gate媛 ?ы븿?쒕떎. SCRFD/YuNet backend? YuNet tiling ?ㅽ뿕, auto-tune CPU/GPU ?좏깮 蹂댁젙 ??`dotnet build FaceShield.sln`? 7媛?湲곗〈 FFmpeg obsolete warning留??④린怨??깃났?덇퀬, `git diff --check`???듦낵?덈떎. 理쒖떊 `-RunExportSmoke`???⑥씪 ?쇨뎬 fast path 異붽? ???ㅼ떆 ?듦낵?덈떎. ?덉쭏 gate??`baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`, CPU 蹂묐젹 `totalMs=32,437ms`?怨? direct export smoke??`bitmapMaskFrames=0`, `directFaceFrames=31`, `maskMs=713`, `totalMs=7,066ms`??? 理쒖떊 `-RunMediumExport` verifier???먮룞 寃異?`totalMs=316,366ms`, export `totalMs=127,750ms`濡??듦낵?덈떎. | 異⑹” |

?꾩옱 寃곕줎:

- 紐⑺몴瑜??꾨즺濡?泥섎━?????녿떎.
- 吏湲덇퉴吏??蹂寃쎌? 湲곕낯 ?덉쭏 ?좎?, 蹂묐젹 泥섎━ ?띾룄, track continuity, 吏㏃? ?ㅽ깘 ?쒓굅, 諛섏そ ?쇨뎬 ?꾨낫 蹂댁〈??蹂닿컯???④퀎??
- ?ㅼ쓬?쇰줈 ?ㅼ젣 紐⑺몴????吏곸젒?곸쑝濡??⑥? ?묒뾽? SCRFD decoder/?꾩쿂由?異붽? 蹂댁젙 ?먮뒗 ?ㅻⅨ detector ?꾨낫 A/B, ?꾩껜 17遺??먮낯 ?먮뒗 洹몄뿉 以?섎뒗 湲?援ш컙 end-to-end ?먮룞 寃異?+ export 寃利? Avalonia GUI smoke?? `FaceTrackRoiRefiner`???ㅼ젣 hit ???援ш컙? 9遺?2珥?clip?먯꽌 ?뺣낫?덉?留? 媛뺥븳 2李?紐⑤뜽 verifier???ㅼ젣 紐⑤뜽 寃利앹? ?꾩쭅 ?녿떎.

## 2026-05-12 SCRFD ?몃? 紐⑤뜽 A/B 1李??몃? 紐⑤뜽 ?꾨낫??Hugging Face `RuteNL/SCRFD-face-detection-ONNX`??`500m.onnx`? `10g_bnkps.onnx`瑜?`.tmp/models/`?먮쭔 ?대젮諛쏆븘 ?뚯뒪?명뻽?? ?대떦 紐⑤뜽 移대뱶??Apache-2.0?쇰줈 ?쒖떆?섏?留? upstream pretrained model 異쒖쿂媛 InsightFace?대?濡?諛고룷/?곸슜 ?ъ슜? 蹂꾨룄 ?뺤씤???꾩슂?섎떎. 紐⑤뜽 ?뚯씪? repo???ы븿?섏? ?딅뒗??

異붽?/蹂寃??뚯씪:

- `Services/FaceDetection/ScrfdOnnxDetector.cs`
- `Services/FaceDetection/ScrfdOnnxDetectorOptions.cs`
- `Services/FaceDetection/YuNetOnnxDetector.cs`
- `Services/FaceDetection/YuNetOnnxDetectorOptions.cs`
- `Services/FaceDetection/FaceDetectorBackend.cs`
- `Services/FaceDetection/FaceDetectorFactory.cs`
- `Services/FaceDetection/FaceDetectorFactoryOptions.cs`
- `scripts/run-srcTest-smoke.ps1`
- `scripts/inspect-onnx-outputs.ps1`

寃利?

- `scripts/inspect-onnx-outputs.ps1 -ModelPath .tmp/models/scrfd_500m.onnx` 寃곌낵 input? `input.1=1x3x640x640`, output? `score_8/16/32`, `bbox_8/16/32` 援ъ“???
- `scrfd_500m.onnx` ?⑤룆 optimized smoke??`.tmp/srcTest-smoke/smoke-0600-3s.mp4`?먯꽌 ?ㅽ뻾?먮떎. `ConfidenceThreshold=0.25` 湲곗? `totalMs=13,174ms`, `faceMaskFrames=23`?댁뿀?쇰굹 FaceONNX baseline怨?鍮꾧탳?섎㈃ `baselineFrames=19`, `optimizedFrames=23`, `onlyBaseline=13`, `onlyOptimized=17`, `avgBestIou=0.001`, `passed=False`???
- `scrfd_500m.onnx`瑜?`ConfidenceThreshold=0.5`濡??щ━硫?`totalMs=11,166ms`源뚯? 以꾩뿀吏留?`faceMaskFrames=0`?댁뿀?? ?뚮젮吏??쇨뎬 援ш컙?먯꽌 理쒖쥌 留덉뒪?ш? 0?꾨젅?꾩씠誘濡??꾩옱 pipeline 寃곌낵濡쒕뒗 遺덊빀寃⑹씠??
- `scrfd_10g_bnkps.onnx` ?⑤룆 optimized smoke??`totalMs=27,819ms`, `faceMaskFrames=19`濡?baseline frame ?섏? 媛숈븯??
- 洹몃윭??`scrfd_10g_bnkps.onnx` A/B gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=16`, `onlyOptimized=16`, `avgBestIou=0.010`, `passed=False`???
- insightface 諛⑹떇??留욎텣 letterbox + RGB ?꾩쿂由щ? 異붽?濡??곸슜?덉?留? `scrfd_10g_bnkps.onnx`??`faceMaskFrames=2`, `onlyBaseline=17`, `avgBestIou=0.290`, `passed=False`濡????섎튌議뚮떎.
- stretch + BGR 議고빀??`scrfd_10g_bnkps.onnx`?먯꽌 `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=16`, `onlyOptimized=16`, `avgBestIou=0.010`, `passed=False`濡?湲곗〈 stretch + RGB? ?숈씪?섍쾶 ?ㅽ뙣?덈떎.

?먯젙:

- SCRFD backend???ㅼ젣 紐⑤뜽 濡쒕뱶? ?먮룞 紐⑥옄?댄겕 ?뚯씠?꾨씪???ㅽ뻾源뚯? 媛?ν븯??
- ?꾩옱 decoder/?꾩쿂由?議고빀 ?먮뒗 ?꾨낫 紐⑤뜽? FaceONNX baseline-diff gate瑜??듦낵?섏? 紐삵븳?? letterbox/RGB, stretch/RGB, stretch/BGR 以??듦낵??議고빀? ?녿떎.
- ??寃곌낵留뚯쑝濡?SCRFD 紐⑤뜽 ?먯껜媛 ?ㅼ젣 ?쇨뎬 寃異?湲곗??먯꽌 ??몃떎怨??⑥젙?섏? ?딅뒗?? ?ㅻ쭔 `avgBestIou=0.001~0.010` ?섏???醫뚰몴 遺덉씪移섏? `faceMaskFrames=0` 寃곌낵媛 ?덉뼱 ?꾩옱 adapter/?꾩쿂由??꾩쿂由?議고빀? 異붿쿇?????녿떎.
- ?띾룄留?蹂대㈃ SCRFD 500M? 留ㅼ슦 鍮좊Ⅴ吏留??꾩옱 pipeline 異쒕젰? 湲곗〈 ?숈옉怨??ш쾶 ?ㅻⅤ怨? SCRFD 10G???쇰? ?꾨젅???섎뒗 留욎?留?醫뚰몴/援ш컙 ?쇱튂媛 遺議깊븯??
- ?곕씪??湲곕낯 detector 援먯껜??蹂대쪟?쒕떎. ?ㅼ쓬 ?꾨낫??letterbox ?낅젰, RGB/BGR ?낅젰 ?듭뀡, bbox decode 諛⑹떇蹂?A/B, ?먮뒗 ?ㅻⅨ 紐⑤뜽 怨꾩뿴(YuNet/RetinaFace/YOLO-face) 鍮꾧탳??

## 2026-05-12 YuNet ?몃? 紐⑤뜽 A/B 1李?OpenCV Zoo??`face_detection_yunet_2023mar.onnx`瑜?`.tmp/models/`?먮쭔 ?대젮諛쏆븘 ?뚯뒪?명뻽?? OpenCV Zoo README 湲곗? YuNet? MIT License?대ŉ, 2023-March 紐⑤뜽? WIDER Face 湲곗? AP hard 0.7503?쇰줈 怨듦컻?섏뼱 ?덈떎. 紐⑤뜽 ?뚯씪? repo???ы븿?섏? ?딅뒗??

援ы쁽:

- `FaceDetectorBackend.YuNetOnnx`瑜?異붽??덈떎.
- `YuNetOnnxDetector`瑜?異붽???OpenCV `FaceDetectorYN`??怨듦컻 postprocess? 媛숈? 諛⑹떇?쇰줈 `cls_8/16/32`, `obj_8/16/32`, `bbox_8/16/32`瑜?decode?쒕떎.
- `scripts/run-srcTest-smoke.ps1 -YuNetModelPath <model.onnx>`濡?FaceONNX baseline ?鍮?YuNet optimized A/B瑜??ㅽ뻾?????덈떎.
- `-YuNetUseTiling`, `-YuNetTileOnly`, `-YuNetTileColumns`, `-YuNetTileRows`, `-YuNetTileOverlapRatio` ?듭뀡??異붽???4K ?먮낯??640 ?낅젰 ?섎굹濡쒕쭔 ?뺤텞?섏? ?딅뒗 tile/multi-region ?ㅽ뿕???ㅽ뻾?????덇쾶 ?덈떎.

寃利?

- `scripts/inspect-onnx-outputs.ps1 -ModelPath .tmp/models/face_detection_yunet_2023mar.onnx` 寃곌낵 input? `input=1x3x640x640`, output? `cls_8/16/32`, `obj_8/16/32`, `bbox_8/16/32`, `kps_8/16/32` 12媛쒖???
- `.tmp/srcTest-smoke/smoke-0600-3s.mp4` YuNet ?⑤룆 optimized smoke??`totalMs=9,825ms`, `detectMs=15,890ms`, `faceMaskFrames=33`, ROI refiner `attempts=7`, `hits=7`濡?留ㅼ슦 鍮⑤옄??
- 媛숈? 援ш컙 FaceONNX baseline ?鍮?A/B gate??`baselineFrames=19`, `optimizedFrames=33`, `onlyBaseline=4`, `onlyOptimized=18`, `avgBestIou=0.277`, `passed=False`???
- `ConfidenceThreshold=0.6`? YuNet-only ?꾨낫瑜?以꾩?吏留?`faceMaskFrames=0`?댁뿀?? ?뚮젮吏??쇨뎬 援ш컙?먯꽌 理쒖쥌 留덉뒪?ш? 0?꾨젅?꾩씠誘濡??꾩옱 pipeline 寃곌낵濡쒕뒗 遺덊빀寃⑹씠??
- `-YuNetUseTiling` ?⑤룆 optimized smoke??`totalMs=41,791ms`, `detectMs=82,467ms`, `faceMaskFrames=51`, ROI refiner `attempts=28`, `hits=27`?댁뿀??
- 媛숈? tiling ?ㅼ젙??FaceONNX baseline ?鍮?A/B gate??`baselineFrames=19`, `optimizedFrames=51`, `onlyBaseline=4`, `onlyOptimized=36`, `avgBestIou=0.272`, `minBestIou=0.000`, `passed=False`???
- full frame ?낅젰??鍮쇨퀬 tile留??뚮━??`-YuNetUseTiling -YuNetTileOnly` A/B gate???ㅽ뻾?덈떎. 寃곌낵??`totalMs=34,976ms`, `detectMs=69,111ms`, `faceMaskFrames=38`, ROI refiner `attempts=25`, `hits=24`?怨? FaceONNX baseline ?鍮?`baselineFrames=19`, `optimizedFrames=38`, `onlyBaseline=5`, `onlyOptimized=24`, `avgBestIou=0.163`, `minBestIou=0.000`, `passed=False`???

?먯젙:

- YuNet backend???ㅼ젣 紐⑤뜽 濡쒕뱶? ?먮룞 紐⑥옄?댄겕 ?뚯씠?꾨씪???ㅽ뻾源뚯? 媛?ν븯??
- ?띾룄???꾩옱 ?꾨낫 以?媛??醫뗭?留? 4K ?먮낯??640 怨좎젙 ?낅젰?쇰줈 以꾩씠??援ъ“? ?꾩옱 threshold 議고빀?먯꽌??FaceONNX baseline ?덉쭏 gate瑜??듦낵?섏? 紐삵븳??
- tiling? ?묒? ?쇨뎬 ?꾨낫瑜??섎━?????YuNet-only ?꾨낫???ш쾶 ?섎━怨??⑥씪 YuNet ?鍮??띾룄 ?댁젏??以꾩뿀?? tile-only??full+tile蹂대떎 鍮좊Ⅴ怨?YuNet-only frame ?섎뒗 以꾩뿀吏留?baseline怨쇱쓽 醫뚰몴/援ш컙 ?쇱튂媛 ???섎뭅?? ?꾩옱 2x2 tiling ?ㅼ젙? 湲곕낯 detector 援먯껜 ?꾨낫媛 ?꾨땲??
- 湲곕낯 detector 援먯껜??蹂대쪟?쒕떎. YuNet? 鍮좊Ⅸ 1李??꾨낫/ROI verifier ?꾨낫濡??④린?? 湲곕낯 ?밴꺽 ?꾩뿉??threshold curve, tile-only/full+tile 鍮꾧탳, ?ㅽ깘 ?꾪꽣, ???곹빀???泥?紐⑤뜽(RetinaFace/YOLO-face ????蹂꾨룄濡?寃利앺빐???쒕떎.

## 2026-05-12 ?꾩옱 ?섍꼍 ?ш?利?怨꾪쉷
湲곗〈 SCRFD/YuNet A/B? auto tune 寃利앹? GPU媛 ?녿뒗 ?명듃遺??섍꼍?먯꽌 ?섑뻾??寃곌낵媛 ?욎뿬 ?덈떎. ?곕씪??洹?寃곌낵??CPU-only ??ъ뼇 湲곗???李멸퀬媛믪쑝濡?蹂닿퀬, ?꾩옱 紐⑺몴 ?섍꼍?먯꽌 ?ㅼ떆 寃利앺븳??

而ㅻ??덉??댁뀡/臾멸뎄 湲곗?:

- ?ъ슜?먯뿉寃?蹂댁씠??臾멸뎄, 臾몄꽌 湲곕줉, 寃利?寃곌낵 ?ㅻ챸, UI 臾멸뎄?먯꽌??諛섎쭚???덈? ?ъ슜?섏? ?딅뒗??
- 紐⑤뱺 ?ㅻ챸? 議대뙎留??먮뒗 以묐┰?곸씤 湲곗닠 臾몄껜濡??묒꽦?쒕떎.
- 湲됲븳 ?묒뾽 硫붾え?쇰룄 ?ъ슜?먮? ?ν븳 ?쒗쁽?먮뒗 諛섎쭚, 紐낅졊議? 鍮꾪븯 ?쒗쁽???④린吏 ?딅뒗??
- Git 愿???묒뾽? 蹂꾨룄 ?뺤씤??諛쏅뒗?? ?뱁엳 `push`, `pull`, ?묒뾽 痍⑥냼, ?섎룎由ш린泥섎읆 ?먭꺽/釉뚮옖移??묒뾽 ?곹깭???곹뼢??二쇰뒗 ?묒뾽? ?ъ슜???뺤씤 ??吏꾪뻾?쒕떎.
- 洹???紐⑺몴 踰붿쐞 ?덉쓽 肄붾뱶 援ы쁽, 肄붾뱶 ?섏젙, 臾몄꽌 ?섏젙, 濡쒖뺄 ?뚯뒪?? smoke ?ㅽ뻾, 寃利??ㅽ겕由쏀듃 ?ㅽ뻾? 留ㅻ쾲 ?섎Щ吏 ?딄퀬 ?먯쑉?곸쑝濡?吏꾪뻾?쒕떎.
- ?먯쑉 吏꾪뻾???묒뾽? 寃곌낵? 洹쇨굅瑜?臾몄꽌? 理쒖쥌 蹂닿퀬???④릿??
- ?ㅽ뻾 ?섍꼍??沅뚰븳 ?쒖뒪???뚮Ц???꾧뎄 ?뱀씤 ?꾨＼?꾪듃媛 ?꾩슂??寃쎌슦媛 ?덉쓣 ???덉?留? ?묒뾽 ?먮떒 ?먯껜????湲곗????곕씪 ?먯쑉 吏꾪뻾?쒕떎.

?대쾲 ?쇱슫?쒖쓽 紐⑺몴??理쒖긽 寃利??덉쭏怨?鍮좊Ⅸ 泥섎━ ?띾룄瑜??숈떆???ъ꽦?섎뒗 寃껋씠?? ?곗꽑?쒖쐞???덉쭏??癒쇱? ?듦낵?쒗궎怨? ?듦낵???꾨낫??以묒뿉??媛??鍮좊Ⅸ ?ㅼ젙??李얜뒗 諛⑹떇?쇰줈 ?붾떎.

- ?쇨뎬 誘명깘? ?덉슜?섏? ?딅뒗?? ?묒? ?쇨뎬, 癒??쇨뎬, 諛섏そ ?쇨뎬, 怨좉컻媛 ?뚯븘媛??쇨뎬???몄텧?섎㈃ ?ㅽ뙣濡?蹂몃떎.
- ?쇨뎬???꾨땶 臾쇨굔 ?ㅽ깘???덉슜?섏? ?딅뒗?? 遺덊븘?뷀븳 紐⑥옄?댄겕???곸긽 ?덉쭏???⑥뼱?⑤━誘濡??ㅽ뙣濡?蹂몃떎.
- 媛숈? ?쇨뎬??紐⑥옄?댄겕媛 以묎컙???щ씪議뚮떎 ?섑??섎뒗 源쒕컯?꾨룄 ?ㅽ뙣濡?蹂몃떎.
- 紐⑥옄?댄겕 諛뺤뒪媛 ?쇨뎬???곕씪 ?먯뿰?ㅻ읇寃??댁뼱吏吏 ?딄퀬 ?嫄곕굹 ?붾뱾由щ㈃ ?ㅽ뙣濡?蹂몃떎.
- ??踰??щ엺 ?쇨뎬濡??뺤젙??track? ?붾㈃?먯꽌 ?ㅼ젣濡??щ씪吏嫄곕굹 scene cut/???꾩튂 蹂?붾줈 醫낅즺 ?먯젙?섍린 ?꾧퉴吏 紐⑥옄?댄겕媛 ?좎??섏뼱???쒕떎.
- ?뺤젙 track??detector 誘명깘 ?뚮Ц??1~紐??꾨젅??鍮꾩뼱??利됱떆 紐⑥옄?댄겕瑜??쒓굅?섏? ?딅뒗?? ?댁쟾 ?대룞 諛⑺뼢怨??ш린 蹂?붾줈 ?덉륫/蹂닿컙???좎??섍퀬, ROI ?ш?異쒕줈 ?뺤씤?쒕떎.
- ?뺤젙 track 醫낅즺??湲?誘명깘, ?붾㈃ 諛??대룞, scene cut, 鍮꾩젙?곸쟻???꾩튂/?ш린 蹂??媛숈? 紐낇솗??議곌굔???덉쓣 ?뚮쭔 ?덉슜?쒕떎.
- ?띾룄 媛쒖꽑???듭떖 紐⑺몴?? ?ㅻ쭔 ?띾룄?????덉쭏 議곌굔??留뚯”???꾨낫?쇰━ 鍮꾧탳?쒕떎. 鍮좊Ⅴ吏留?誘명깘, ?ㅽ깘, 源쒕컯?꾩씠 ?앷린???ㅼ젙? 湲곕낯媛믪씠??異붿쿇媛믪쑝濡??곗? ?딅뒗??
- 理쒖쥌 ?꾨낫??`誘명깘 0`, `?ㅽ깘 0`, `源쒕컯??0`, `諛뺤뒪 ??理쒖냼??瑜?留뚯”?섎㈃??`[AutoRunSummary].totalMs`? `[ExportRunSummary].totalMs`媛 媛????? 議고빀?댁뼱???쒕떎.

?곕씪???먮룞 gate???섏튂留뚯쑝濡??꾨즺瑜??먮떒?섏? ?딅뒗?? `avgBestIou`, `minBestIou`, `faceMaskFrames`, `removedShort`, `lostFilled` 媛숈? 濡쒓렇???꾨낫瑜?醫곹엳??洹쇨굅??肉먯씠硫? ???援ш컙???≪븞 ?뺤씤?먯꽌 誘명깘/?ㅽ깘/源쒕컯?꾩씠 ?놁뼱???듦낵濡?蹂몃떎.

FaceONNX baseline? 湲곗〈 ???숈옉 蹂댁〈???꾪븳 ?뚭? 湲곗??댁? ?ㅼ젣 ?뺣떟 ?쇰꺼???꾨땲?? A/B 濡쒓렇??`onlyBaseline`? FaceONNX?먮쭔 ?덈뒗 ?꾨낫 frame, `onlyOptimized`??YOLO/SCRFD/YuNet ??optimized detector?먮쭔 ?덈뒗 ?꾨낫 frame???삵븳?? ??媛믪? detector 媛?李⑥씠瑜?李얜뒗 ?좏샇??肉먯씠硫? 怨㏓컮濡??ㅼ젣 `誘명깘` ?먮뒗 `?ㅽ깘`?쇰줈 ?먯젙?섏? ?딅뒗??

?ㅼ젣 ?ㅽ깘/誘명깘 ?먯젙 湲곗?? ?ㅼ쓬怨?媛숈씠 遺꾨━?쒕떎.

- ?ㅼ젣 誘명깘: ?쇰꺼??GT ?먮뒗 ???frame overlay ?≪븞 ?뺤씤?먯꽌 ?щ엺 ?쇨뎬??蹂댁씠?붾뜲 detector 寃곌낵媛 ?녾굅??紐⑥옄?댄겕媛 ?좎??섏? ?딅뒗 寃쎌슦.
- ?ㅼ젣 ?ㅽ깘: ?쇰꺼??GT ?먮뒗 ???frame overlay ?≪븞 ?뺤씤?먯꽌 ?쇨뎬???꾨땶 ??臾쇱껜/諛곌꼍?몃뜲 detector媛 ?쇨뎬濡??≪븘 紐⑥옄?댄겕 ??곸씠 ?섎뒗 寃쎌슦.
- baseline-diff: `onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou`泥섎읆 FaceONNX? optimized detector???꾨낫 ?섎굹 諛뺤뒪 ?뺤쓽媛 ?ㅻⅨ 寃쎌슦.

?곕씪??YOLO媛 FaceONNX蹂대떎 鍮좊Ⅴ怨?`onlyOptimized`瑜?留뚮뱾?붾씪?? 洹??꾨낫媛 ?ㅼ젣 ?쇨뎬?대㈃ recall 媛쒖꽑 媛?μ꽦?쇰줈 ?곕줈 湲곕줉?쒕떎. 諛섎?濡?`onlyBaseline`??FaceONNX媛 留욊퀬 YOLO媛 ??몃떎???살쑝濡??⑥젙?섏? ?딅뒗?? 異붿쿇 ?щ???癒쇱? baseline-diff gate濡?湲곗〈 ?숈옉 蹂????쓣 ?뺤씤?섍퀬, 洹??ㅼ쓬 representative overlay ?먮뒗 GT 湲곗??쇰줈 ?ㅼ젣 ?쇨뎬/鍮꾩뼹援??щ?瑜??뺤씤???먮떒?쒕떎.

?꾩옱 ?쒕낯 GT ?쇰꺼? 湲곗〈 crop review CSV??`label`怨?`verdict`瑜?湲곗??쇰줈 ?곗텧?쒕떎. `optimized + Face`??`YoloTruePositive`, `optimized + NonFace`??`YoloFalsePositive`, `baseline + Face`??`YoloMiss`, `baseline + NonFace`??`FaceOnnxFalsePositive`濡?遺꾨쪟?쒕떎. ??遺꾨쪟???쒕낯 crop ?⑥쐞??face/non-face ?먯젙?대ŉ, ?꾩껜 ?곸긽 frame/track GT瑜??泥댄븯吏 ?딅뒗??

?대쾲 ?ш?利앹뿉?쒕뒗 YuNet???곗꽑 ?쒖쇅?쒕떎.

?쒖쇅 ?댁쑀:

- YuNet? ?띾룄??鍮좊Ⅴ吏留?湲곗〈 A/B?먯꽌 FaceONNX-only/YuNet-only frame怨?baseline 醫뚰몴 遺덉씪移섍? 而몃떎.
- tiling??耳쒕㈃ ?묒? ?쇨뎬 ?꾨낫???섏?留?YuNet-only ?꾨낫???ш쾶 ?섍퀬 ?띾룄 ?댁젏??以꾩뿀??
- ?꾩옱 臾몄젣???듭떖? ?ㅼ젣 ?묒? ?쇨뎬 ?꾨씫, ?ㅼ젣 臾쇨굔 ?ㅺ?異? track 源쒕컯?? 湲?export ?쒓컙?대?濡?YuNet??怨꾩냽 ?쒕떇?섍린蹂대떎 FaceONNX baseline怨?SCRFD ?꾨낫瑜?癒쇱? ?꾩옱 ?섍꼍?먯꽌 ?ㅼ떆 鍮꾧탳?쒕떎.

?꾩옱 ?섍꼍 寃利????

1. `FaceONNX`
   - ?꾩옱 湲곕낯 detector?댁옄 ?덉젙 baseline?대떎.
   - CPU/GPU auto tune 寃곌낵瑜?紐⑤몢 湲곕줉?쒕떎.
   - track ?꾩쿂由? ROI refiner, ?묒? ?쇨뎬 filter媛 耳쒖쭊 ?꾩옱 湲곕낯 寃쎈줈瑜?湲곗??쇰줈 ?붾떎.

2. `SCRFD`
   - ?묒? ?쇨뎬怨?誘명깘 媛먯냼 媛?μ꽦???덈뒗 ?꾨낫濡??ㅼ떆 寃利앺븳??
   - 湲곗〈 ?명듃遺?寃利앹뿉???ㅽ뙣??`500M`, `10G` 寃곌낵???먭린?섏? ?딅릺, ?꾩옱 ?섍꼍?먯꽌 媛숈? clip怨?媛숈? quality gate濡??ㅼ떆 ?뺤씤?쒕떎.
   - 媛?ν븯硫?`2.5G` 怨꾩뿴??異붽? ?꾨낫濡?寃?좏븳??

?대쾲 寃利앹뿉??怨좎젙??湲곕낯 議곌굔:

- `DownscaleRatio=1.0`
- `DetectEveryNFrames=1`
- `UseTracking=true`
- `ParallelDetectorCount`??`2`? `4`瑜?紐⑤몢 痢≪젙?쒕떎.
- threshold???꾩옱 ?ъ슜??湲곗?媛?`DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`???쒖옉?먯쑝濡??ъ슜?쒕떎. ??媛믪? 怨좎젙 寃곕줎???꾨땲??
- FaceONNX? SCRFD 媛곴컖??????ㅼ젣 ?꾨씫 0, ?ㅼ젣 ?ㅺ?異?0, 源쒕컯??0??媛??媛源뚯슫 threshold 議고빀??李얘퀬, 寃利앸맂 議고빀??湲곕낯媛??꾨낫濡?臾몄꽌?뷀븳??
- ?덉쭏 鍮꾧탳 ?꾩뿉??sparse 寃異? downscale, threshold ?꾪솕濡??띾룄瑜??살? ?딅뒗??

寃利?吏??

- ?묒? ?쇨뎬 誘명깘 frame ??- 臾쇨굔 ?ㅽ깘 frame ??- 紐⑥옄?댄겕 源쒕컯??frame ??- 諛뺤뒪 ???붾뱾由?援ш컙 ??- `faceMaskFrames`
- track ?꾩쿂由?濡쒓렇: `tracks`, `filled`, `lostFilled`, `removedShort`, `rewritten`
- ROI refiner 濡쒓렇: `attempts`, `hits`, `seeks`, `decoded`, `elapsedMs`
- `[AutoRunSummary] totalMs`, `detectMs`, provider
- `[ExportRunSummary] totalMs`, `maskMs`, `swsToBgraMs`, `swsToEncMs`, `encodeMs`
- threshold sweep 寃곌낵: `DetectionThreshold`, `ConfidenceThreshold`, `NmsThreshold`蹂?誘명깘/?ㅽ깘/源쒕컯???띾룄 蹂??- ?≪븞 ?뺤씤 寃곌낵: 紐⑥옄?댄겕 源쒕컯?? 諛뺤뒪 ?? ?묒? ?쇨뎬 ?꾨씫, 臾쇨굔 ?ㅽ깘

吏꾪뻾 ?쒖꽌:

1. ?꾩옱 湲곕낯 `FaceONNX`濡????clip?ㅼ쓣 ?ㅼ떆 ?ㅽ뻾?쒕떎.
2. 媛숈? clip?먯꽌 `ParallelDetectorCount=2`? `4`瑜?鍮꾧탳?쒕떎. ?먮떒? `detectMs`媛 ?꾨땲??wall-clock??`totalMs`瑜??곗꽑?쒕떎.
3. SCRFD ?꾨낫 紐⑤뜽??媛숈? clip?먯꽌 ?ㅽ뻾?쒕떎.
4. SCRFD媛 FaceONNX ?鍮??묒? ?쇨뎬 誘명깘??以꾩씠?붿? 癒쇱? 蹂몃떎.
5. FaceONNX? SCRFD 媛곴컖?????threshold sweep???ㅽ뻾?쒕떎.
6. threshold sweep? detection/confidence瑜???떠 誘명깘??以꾩씠??諛⑺뼢怨? confidence/NMS瑜??щ젮 ?ㅽ깘??以꾩씠??諛⑺뼢??紐⑤몢 ?ы븿?쒕떎.
7. threshold ?꾨낫留덈떎 ?묒? ?쇨뎬 誘명깘, 臾쇨굔 ?ㅽ깘, 源쒕컯?? track 蹂댁젙 濡쒓렇, ?띾룄瑜?湲곕줉?쒕떎.
8. SCRFD媛 ?ㅽ깘???섎━硫?threshold, NMS, ?꾩쿂由??꾪꽣 議고빀??議곗젙?쒕떎.
9. FaceONNX? SCRFD 以??섎굹瑜?湲곕낯媛믪쑝濡?諛붾줈 援먯껜?섏? ?딄퀬, `Balanced/Accurate` 媛숈? ?대? 紐⑤뱶 ?꾨낫濡??붾떎.
10. representative clip?먯꽌 ?듦낵???ㅼ뿉留???湲?援ш컙怨?export ?ы븿 smoke瑜??ㅽ뻾?쒕떎.

?먯젙 湲곗?:

- SCRFD媛 FaceONNX蹂대떎 ?ㅼ젣 ?묒? ?쇨뎬 ?꾨씫??以꾩씠怨? ?ㅼ젣 臾쇨굔 ?ㅺ?異쒓낵 紐⑥옄?댄겕 源쒕컯?꾩쓣 留뚮뱾吏 ?딆쓣 ?뚮쭔 ?ㅼ쓬 ?꾨낫濡??좎??쒕떎.
- SCRFD媛 鍮좊Ⅴ?붾씪???ㅼ젣 ?꾨씫/?ㅺ?異?源쒕컯??諛뺤뒪 ?먯씠 ?앷린硫?湲곕낯 ?밴꺽?섏? ?딅뒗??
- threshold 湲곕낯媛믪? ?섎뱶肄붾뵫???꾩옱 媛믪씠 ?꾨땲?? ?꾩옱 ?섍꼍 ???援ш컙?먯꽌 媛??醫뗭? ?덉쭏/?띾룄 洹좏삎??蹂댁씤 寃利앷컪?쇰줈 ?뺥븳??
- 理쒖쥌 臾몄꽌?먮뒗 detector蹂?異붿쿇 threshold? 洹쇨굅瑜??④릿?? ?? `FaceONNX 湲곕낯 ?꾨낫: detection=?, confidence=?, nms=?`, `SCRFD ?꾨낫: confidence=?, nms=?`.
- FaceONNX媛 ?꾩옱 ?섍꼍?먯꽌??媛???덉젙?곸씠硫?湲곕낯 detector???좎??섍퀬, SCRFD???뺥솗???곗꽑 ?ㅽ뿕 ?듭뀡?쇰줈 ?④릿??
- YuNet? ?대쾲 ?쇱슫?쒖뿉?쒕뒗 ?쒖쇅?섍퀬, FaceONNX/SCRFD 鍮꾧탳媛 ?앸궃 ??fast mode ?꾨낫濡??ㅼ떆 蹂쇱? 寃곗젙?쒕떎.

### 2026-05-13 ?꾩옱 ?섍꼍 1李??ㅽ뻾 湲곕줉

???clip:

- `.tmp/srcTest-smoke/current-0030-2s.mp4`
- ?먮낯: `/mnt/d/WorkSpace/src/260102_two4.mp4`??00:00:30遺??2珥?援ш컙
- 怨듯넻 議곌굔: `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `UseTracking=true`, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, export ?ы븿

肄붾뱶 蹂寃?

- `Services/Analysis/FaceTrackInterpolator.cs`
- ?뺤젙 track lost-fill 議곌굔?먯꽌 `IsSmallTrack(track, options)` ?쒖쇅 議곌굔???쒓굅?덈떎.
- 紐⑹쟻? ??踰??щ엺 ?쇨뎬濡??뺤젙???묒? ?쇨뎬 track??detector 誘명깘 1~紐??꾨젅???뚮Ц??諛붾줈 ?딄린吏 ?딄쾶 ?섎뒗 寃껋씠??
- 吏㏃? ?⑤컻/??좊ː track ?쒓굅 濡쒖쭅? ?좎??섎?濡? 1~2?꾨젅?꾩쭨由??묒? 臾쇨굔 ?ㅽ깘???뺤젙 track泥섎읆 ?앷퉴吏 ?좎??섎뒗 蹂寃쎌? ?꾨땲??

FaceONNX 蹂묐젹 2 ?ш?利?

- 蹂寃??? `faceMaskFrames=16`, `onlyBaseline=58,59`, `passed=False`
- 蹂寃??? `faceMaskFrames=19`, `onlyBaseline=none`, `onlyOptimized=none`
- ?꾩쿂由?濡쒓렇: `tracks=3`, `lostFilled=3`, `lostFrames=58,59,60`, `removedShort=1`, `rewritten=19`
- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=2`, `detectMs=32629`, `totalMs=16732`
- `[ExportRunSummary]`: `directFaceFrames=19`, `totalMs=2897`
- `[SmokeQualityGate]`: `passed=False`, `frameMatchOk=True`, `iouOk=False`, `avgBestIou=0.755`, `minBestIou=0.560`
- ?먮떒: ?묒? ?쇨뎬 ?앸?遺?源쒕컯???꾨씫? 蹂댁젙?먯?留? baseline ?鍮?box ?꾩튂/?ш린 李⑥씠媛 而ㅼ꽌 理쒖긽 寃利??덉쭏 ?듦낵???꾨땲??

FaceONNX 蹂묐젹 4 ?ш?利?

- `faceMaskFrames=19`, `onlyBaseline=none`, `onlyOptimized=none`
- ?꾩쿂由?濡쒓렇: `tracks=3`, `lostFilled=3`, `lostFrames=58,59,60`, `removedShort=1`, `rewritten=19`
- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=4`, `detectMs=73995`, `totalMs=19601`
- `[ExportRunSummary]`: `directFaceFrames=19`, `totalMs=2879`
- `[SmokeQualityGate]`: `passed=False`, `frameMatchOk=True`, `iouOk=False`, `avgBestIou=0.755`, `minBestIou=0.560`
- ?먮떒: ??clip怨??꾩옱 PC?먯꽌??4?ㅻ젅?쒓? 2?ㅻ젅?쒕낫???먮━?? 4?ㅻ젅?쒓? ??긽 鍮좊Ⅴ?ㅺ퀬 蹂?洹쇨굅???꾩쭅 ?녿떎.

SCRFD 500M ?ш?利?

- 紐⑤뜽: `.tmp/models/scrfd_500m.onnx`
- RGB/letterbox 湲곕낯 ?낅젰 寃곌낵: `faceMaskFrames=0`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `parallel=2`, `detectMs=5714`, `totalMs=5105`
- filter 濡쒓렇: `regular=0`, `small=0`, `rejected=54`, `statsRejected=3`
- `[SmokeQualityGate]`: `passed=False`, `onlyBaseline=37,38,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60`
- BGR ?낅젰 寃곌낵??`faceMaskFrames=0`
- BGR `[AutoRunSummary]`: `detectMs=5393`, `totalMs=3889`
- ?먮떒: 異붾줎 ?띾룄??鍮좊Ⅴ吏留??꾩옱 decode/filter 議고빀?먯꽌??理쒖쥌 留덉뒪?ш? 0?꾨젅?꾩씠誘濡??꾩옱 pipeline 湲곗? 遺덊빀寃⑹씠?? ??寃곌낵留뚯쑝濡?SCRFD 500M 紐⑤뜽 ?먯껜媛 ?ㅼ젣 ?뺣떟 湲곗??먯꽌 ??몃떎怨??⑥젙?섏? ?딅뒗??

SCRFD 10G ?ш?利?

- 紐⑤뜽: `.tmp/models/scrfd_10g_bnkps.onnx`
- RGB/letterbox 湲곕낯 ?낅젰 寃곌낵: `faceMaskFrames=0`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `parallel=2`, `detectMs=16131`, `totalMs=8539`
- filter 濡쒓렇: `regular=0`, `small=0`, `rejected=26`, `statsRejected=0`
- stretch ?낅젰 寃곌낵??`faceMaskFrames=0`
- stretch `[AutoRunSummary]`: `detectMs=16897`, `totalMs=8841`
- ?먮떒: 500M蹂대떎 ?먮━怨??꾩옱 ?ㅼ젙?먯꽌????떆 理쒖쥌 留덉뒪?ш? 0?꾨젅?꾩씠?? SCRFD??紐⑤뜽 ?깅뒫 ?됯? ?댁쟾???꾩옱 SCRFD decode, 醫뚰몴 蹂?? ?꾩쿂由??꾪꽣 ?명솚?깆쓣 癒쇱? ?뺤씤?댁빞 ?쒕떎.

1李?寃곕줎:

- ?꾩옱 湲곕낯 FaceONNX???묒? ?쇨뎬 ?앸?遺??꾨씫???꾩쿂由щ줈 蹂듦뎄?????덉쓬???뺤씤?덈떎.
- `ParallelDetectorCount=2`媛 ??clip?먯꽌??`4`蹂대떎 鍮좊Ⅴ??
- SCRFD 500M/10G???먯떆 detector媛 ?꾨낫瑜??쇰? 諛섑솚?섏?留? 理쒖쥌 ?꾪꽣瑜??듦낵?섏? 紐삵빐 紐⑥옄?댄겕媛 0?꾨젅?꾩씠??
- ?곕씪??SCRFD瑜?湲곕낯媛??꾨낫濡??먮떒?섍린 ?꾩뿉 raw box 醫뚰몴, aspect ratio, area ratio, confidence 遺꾪룷, `MultiplyBboxByStride`, letterbox/stretced ?낅젰, ?꾪꽣 湲곗???癒쇱? 怨꾩륫?댁빞 ?쒕떎.
- threshold 湲곕낯媛믪? ?꾩쭅 ?뺤젙?섏? ?딅뒗?? ?꾩옱 `0.2/0.25/0.7`? ?쒖옉?먯씪 肉먯씠硫? FaceONNX? SCRFD 媛곴컖 蹂꾨룄??sweep怨??≪븞 ?뺤씤???꾩슂?섎떎.

### 2026-05-13 `260101_oneday6.mp4` 3珥?援ш컙 A/B

?ъ슜??吏???뚯뒪???먮낯:

- `D:\WorkSpace\src\260101_oneday6.mp4`
- 湲몄씠: ??608.7珥?- 寃利?clip: `.tmp/srcTest-smoke/oneday6-0030-3s.mp4`
- ?앹꽦 議곌굔: ?먮낯 00:00:30遺??3珥?援ш컙
- 怨듯넻 議곌굔: `DownscaleRatio=1.0`, `DetectEveryNFrames=1`, `UseTracking=true`, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, export ?ы븿
- ?대쾲 ?쇱슫?쒖뿉??YuNet? ?ㅽ뻾?섏? ?딆븯??

FaceONNX baseline:

- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-single`, `totalFrames=91`, `processed=90`, `detects=90`, `parallel=1`
- ????ㅽ뻾媛? `detectMs=32055`, `totalMs=33123`
- track 蹂댁젙: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- ROI refiner: `attempts=11`, `hits=0`, `seeks=2`, `decoded=17`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=34`, `totalMs=5122`

FaceONNX parallel 2:

- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=2`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=59832`, `totalMs=30995`
- track 蹂댁젙: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- ROI refiner: `attempts=11`, `hits=0`, `seeks=2`, `decoded=17`, `elapsedMs=2557`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=26`, `totalMs=4952`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`
- `[SmokeQualityGate]`: `passed=True`
- ?먮떒: baseline怨??꾨젅??諛뺤뒪媛 ?꾩쟾???쇱튂?덈떎. ??援ш컙?먯꽌??baseline蹂대떎 wall-clock??議곌툑 鍮좊Ⅴ??

FaceONNX parallel 4:

- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `parallel=4`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=113143`, `totalMs=30003`
- track 蹂댁젙: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- ROI refiner: `attempts=11`, `hits=0`, `seeks=2`, `decoded=17`, `elapsedMs=2480`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=27`, `totalMs=5001`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`
- `[SmokeQualityGate]`: `passed=True`
- ?먮떒: ??clip?먯꽌??4?ㅻ젅?쒓? AutoRunSummary wall-clock 湲곗??쇰줈 2?ㅻ젅?쒕낫??議곌툑 鍮좊Ⅴ?? ?ㅻ쭔 export totalMs??2?ㅻ젅?쒓? 議곌툑 ??떎.

SCRFD 500M parallel 2:

- 紐⑤뜽: `.tmp/models/scrfd_500m.onnx`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=2`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=10657`, `totalMs=7843`
- filter 濡쒓렇: `regular=5`, `small=0`, `rejected=4`, `statsRejected=8`
- track 蹂댁젙: `tracks=3`, `filled=6`, `lostFilled=0`, `lostFrames=none`, `removedShort=2`, `rewritten=9`
- ROI refiner: `attempts=6`, `hits=6`, `seeks=1`, `decoded=7`, `elapsedMs=1053`
- `faceMaskFrames=9`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=9`, `maskMs=4`, `totalMs=5229`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=9`, `common=6`, `onlyBaseline=13`, `onlyOptimized=3`, `avgBestIou=0.000`, `minBestIou=0.000`
- `[SmokeCompareFrames]`: `onlyBaseline=0,20,33,34,35,36,37,41,42,43,44,45,46`, `onlyOptimized=11,12,13`
- `[SmokeQualityGate]`: `passed=False`
- ?먮떒: 誘명깘??留롪퀬 optimized-only frame???덉뼱 ?ㅽ깘 ?꾪뿕???덈떎. ?덉쭏 議곌굔 ?ㅽ뙣??

SCRFD 500M parallel 4:

- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=4`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=16163`, `totalMs=11396`
- filter 濡쒓렇: `regular=5`, `small=0`, `rejected=4`, `statsRejected=8`
- track 蹂댁젙: `tracks=3`, `filled=6`, `lostFilled=0`, `lostFrames=none`, `removedShort=2`, `rewritten=9`
- ROI refiner: `attempts=6`, `hits=6`, `seeks=1`, `decoded=7`, `elapsedMs=1036`
- `faceMaskFrames=9`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=9`, `maskMs=5`, `totalMs=4739`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=9`, `common=6`, `onlyBaseline=13`, `onlyOptimized=3`, `avgBestIou=0.000`, `minBestIou=0.000`
- `[SmokeQualityGate]`: `passed=False`
- ?먮떒: 2?ㅻ젅?쒕낫??AutoRunSummary wall-clock???먮━怨??덉쭏 ?ㅽ뙣 ?묒긽? 媛숇떎.

SCRFD 10G parallel 2:

- 紐⑤뜽: `.tmp/models/scrfd_10g_bnkps.onnx`
- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=2`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=24897`, `totalMs=13467`
- filter 濡쒓렇: `regular=13`, `small=0`, `rejected=18`, `statsRejected=0`
- track 蹂댁젙: `tracks=3`, `filled=7`, `lostFilled=0`, `lostFrames=none`, `removedShort=1`, `rewritten=19`
- ROI refiner: `attempts=7`, `hits=0`, `seeks=1`, `decoded=11`, `elapsedMs=1830`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=141`, `totalMs=4947`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `common=0`, `onlyBaseline=19`, `onlyOptimized=19`
- `[SmokeCompareFrames]`: `onlyBaseline=0,14,15,16,17,18,19,20,33,34,35,36,37,41,42,43,44,45,46`, `onlyOptimized=50,51,52,53,54,55,56,57,58,59,60,61,62,63,75,76,77,78,79`
- `[SmokeQualityGate]`: `passed=False`
- ?먮떒: 理쒖쥌 frame ?섎뒗 媛숈?留?baseline怨?怨듯넻 frame??0?대떎. baseline-diff 湲곗??쇰줈 湲곗〈 FaceONNX ?쇨뎬 援ш컙怨??꾩쟾???ㅻⅨ 援ш컙???↔퀬 ?덉쑝誘濡??꾩옱 pipeline 湲곗? 遺덊빀寃⑹씠?? ?ㅼ젣 誘명깘/?ㅽ깘 ?뺤젙? overlay ?먮뒗 GT ?뺤씤???꾩슂?섏?留? ???섏튂留뚯쑝濡쒕룄 湲곕낯/accurate ?꾨낫濡??щ┫ ???녿떎.

SCRFD 10G parallel 4:

- `[AutoRunSummary]`: `detector=ScrfdOnnxDetector`, `mode=pipe-parallel`, `parallel=4`, `totalFrames=91`, `processed=90`, `detects=90`, `detectMs=258485`, `totalMs=66993`
- filter 濡쒓렇: `regular=13`, `small=0`, `rejected=18`, `statsRejected=0`
- track 蹂댁젙: `tracks=3`, `filled=7`, `lostFilled=0`, `lostFrames=none`, `removedShort=1`, `rewritten=19`
- ROI refiner: `attempts=7`, `hits=0`, `seeks=1`, `decoded=11`, `elapsedMs=8306`
- `faceMaskFrames=19`
- `[ExportRunSummary]`: `frames=91`, `directFaceFrames=19`, `maskMs=355`, `totalMs=8175`
- `[SmokeCompare]`: `baselineFrames=19`, `optimizedFrames=19`, `common=0`, `onlyBaseline=19`, `onlyOptimized=19`
- `[SmokeCompareFrames]`: `onlyBaseline=0,14,15,16,17,18,19,20,33,34,35,36,37,41,42,43,44,45,46`, `onlyOptimized=50,51,52,53,54,55,56,57,58,59,60,61,62,63,75,76,77,78,79`
- `[SmokeQualityGate]`: `passed=False`
- ?먮떒: 10G 4?ㅻ젅?쒕뒗 留ㅼ슦 ?먮━怨??덉쭏???ㅽ뙣?? CPU?먯꽌 10G瑜?4?몄뀡 蹂묐젹濡??뚮━??寃껋? ?꾩옱 PC 湲곗? ?꾨낫媛 ?꾨땲??

`260101_oneday6.mp4` 1李?寃곕줎:

- ??援ш컙??湲곕낯 ?꾨낫??FaceONNX ?좎???
- FaceONNX parallel 2? 4??紐⑤몢 baseline怨??꾩쟾 ?쇱튂?덇퀬 ?덉쭏 gate瑜??듦낵?덈떎.
- AutoRunSummary 湲곗? 理쒓퀬?띿? FaceONNX parallel 4(`totalMs=30003`)?怨? export源뚯? ?ы븿??`ExportRunSummary`??FaceONNX parallel 2(`totalMs=4952`)? 4(`totalMs=5001`)媛 嫄곗쓽 鍮꾩듂?덈떎.
- SCRFD 500M? 鍮좊Ⅴ吏留?`faceMaskFrames=9`濡?baseline 19?꾨젅???鍮?FaceONNX-only frame???ш퀬 SCRFD-only frame???덉뼱 湲곗〈 ?숈옉怨?李⑥씠媛 ?щ떎.
- SCRFD 10G??frame ?섎쭔 留욊퀬 baseline frame ?꾩튂媛 ?꾨? ?щ씪 ?꾩옱 pipeline 湲곗? 遺덊빀寃⑹씠??
- ?대쾲 怨좎젙 threshold `0.2/0.25/0.7` 議곌굔?먯꽌??SCRFD 500M/10G 紐⑤몢 湲곕낯 detector ?꾨낫媛 ?꾨땲??

### ?ㅼ쓬 ?몄뀡 紐⑺몴: SCRFD ?꾩슜 adapter/?꾪꽣 ?뺥빀

?꾩옱 A/B 寃곌낵??SCRFD 紐⑤뜽 ?먯껜??理쒖쥌 ?깅뒫?쇰줈 ?⑥젙?섏? ?딅뒗?? ?꾩옱 肄붾뱶 援ъ“媛 FaceONNX 異쒕젰 ?뱀꽦??留욎떠???덇퀬, SCRFD??媛숈? ?꾩쿂由??꾪꽣/track 湲곗???洹몃?濡??ㅼ뼱媛怨??덈떎. ?곕씪???ㅼ쓬 ?몄뀡??紐⑺몴??SCRFD瑜?FaceONNX ?뚯씠?꾨씪?몄뿉 ?듭?濡??쇱슦??寃껋씠 ?꾨땲?? SCRFD ?꾩슜 adapter? 寃利?濡쒓렇瑜??섏젙?섎㈃??FaceShield ?덉쭏 湲곗???留욎텛??寃껋씠??

紐⑺몴 臾몄옣:

- `FaceONNX baseline? ?좎??쒕떎. SCRFD 500M/10G??detector adapter, bbox decode, 醫뚰몴 蹂듭썝, detector蹂??꾪꽣 ?듭뀡???섏젙?섎㈃??怨듭젙?섍쾶 鍮꾧탳 媛?ν븳 ?곹깭濡?留욎텣?? 理쒖쥌 湲곗?? 誘명깘 0, ?ㅽ깘 0, 源쒕컯??0, ?먮낯 ?댁긽?? ???꾨젅??寃異? tracking on, threshold sweep 湲곕컲 湲곕낯媛??곗젙?대떎.`

?ㅼ쓬 ?몄뀡?먯꽌 ?댁빞 ????

1. SCRFD raw output 寃利?   - output tensor ?대쫫, shape, score tensor, bbox tensor ?쒖꽌瑜?濡쒓렇濡??④릿??
   - ?꾩옱 `PairScoreAndBoxTensors()`媛 ?ㅼ젣 紐⑤뜽 output ?쒖꽌? 留욌뒗吏 ?뺤씤?쒕떎.
   - `GuessStride()` 寃곌낵媛 8/16/32 stride蹂??ㅼ젣 output count? 留욌뒗吏 ?뺤씤?쒕떎.
   - `anchorsPerPoint` 怨꾩궛??紐⑤뜽蹂꾨줈 ?щ컮瑜몄? ?뺤씤?쒕떎.

2. SCRFD bbox decode 寃利?   - `MultiplyBboxByStride=true/false`瑜?鍮꾧탳?쒕떎.
   - `UseLetterboxResize=true/false`瑜?鍮꾧탳?쒕떎.
   - RGB/BGR ?낅젰??鍮꾧탳?쒕떎.
   - raw candidate??`x,y,w,h,area,aspect,confidence`瑜?frame蹂꾨줈 dump?쒕떎.
   - FaceONNX baseline box? SCRFD raw box瑜?媛숈? frame?먯꽌 IoU濡?鍮꾧탳?쒕떎.

3. detector蹂??꾪꽣 遺꾨━
   - ?꾩옱 `AutoMaskGenerator`??硫댁쟻/醫낇슒鍮?skin/edge/luma ?꾪꽣??FaceONNX 異쒕젰??留욎떠???덉쓣 媛?μ꽦???믩떎.
   - `FaceCandidateKind`, `SmallFaceConfidenceMin`, `StatsBypassConfidence`, skin/edge/luma 湲곗???detector蹂??듭뀡?쇰줈 遺꾨━?쒕떎.
   - SCRFD ?꾨낫?먮뒗 FaceONNX??skin/luma ?꾪꽣瑜?洹몃?濡??곸슜?섏? ?딄퀬, 癒쇱? raw detector ?덉쭏???뺤씤????蹂꾨룄 湲곗???留뚮뱺??

4. SCRFD track ?꾩쿂由?遺꾨━
   - `FaceTrackPostProcessOptions`??`StrongConfidence`, `ShortTrackMaxConfidence`, `SmallTrackMaxAreaRatio`, `MinTrackIou`, `MaxCenterShiftRatio`, `MaxAreaChangeRatio`媛 SCRFD confidence/box ?뱀꽦怨?留욌뒗吏 ?뺤씤?쒕떎.
   - SCRFD ?꾩슜 track ?듭뀡 ?먮뒗 detector蹂?option profile??留뚮뱺??
   - SCRFD媛 媛숈? ?щ엺???ㅻⅨ frame 援ш컙?쇰줈 諛???〓뒗 臾몄젣媛 bbox decode 臾몄젣?몄?, track matching 臾몄젣?몄? 遺꾨━?댁꽌 ?먮떒?쒕떎.

5. 寃利??ㅽ겕由쏀듃 蹂닿컯
   - `scripts/run-srcTest-smoke.ps1`??SCRFD debug dump ?듭뀡??異붽??쒕떎.
   - raw detector 寃곌낵? post-filter 寃곌낵瑜??곕줈 異쒕젰?쒕떎.
   - `baselineFrames`, `optimizedFrames`, `onlyBaseline`, `onlyOptimized`肉??꾨땲??raw candidate ?? filter reject ?ъ쑀, detector蹂?confidence 遺꾪룷瑜?湲곕줉?쒕떎.

6. ?ш?利??쒖꽌
   - `D:\WorkSpace\src\260101_oneday6.mp4`?먯꽌 3珥?clip?쇰줈 癒쇱? 鍮좊Ⅴ寃?諛섎났?쒕떎.
   - FaceONNX baseline??怨좎젙?쒕떎.
   - SCRFD 500M遺??raw decode瑜?留욎텣??
   - SCRFD 500M??baseline frame/box??洹쇱젒?섎㈃ 10G瑜?媛숈? 諛⑹떇?쇰줈 ?뺤씤?쒕떎.
   - ?댄썑 threshold sweep?쇰줈 湲곕낯媛??꾨낫瑜??ㅼ떆 ?뺥븳??

?꾨즺 湲곗?:

- SCRFD raw box媛 媛숈? frame?먯꽌 ?ㅼ젣 ?쇨뎬 洹쇱쿂??洹몃젮吏?붿? ?뺤씤?쒕떎.
- SCRFD post-filter ????李⑥씠媛 臾몄꽌?붾맂??
- SCRFD媛 ?ㅽ뙣??寃쎌슦 ?먯씤??紐⑤뜽 誘명깘?몄?, decode ?ㅻ쪟?몄?, FaceONNX 湲곗? ?꾪꽣 ?덈씫?몄? 援щ텇?쒕떎.
- SCRFD 500M/10G 媛곴컖?????`?꾨낫 ?좎?`, `蹂대쪟`, `?먭린` ?먮떒怨?洹쇨굅媛 ?⑤뒗??
- FaceONNX 湲곕낯媛믪쓣 ?좎??좎?, SCRFD瑜?accurate mode ?꾨낫濡??섏?, ?먮뒗 SCRFD 援ы쁽?????섏젙?좎? ?ㅼ쓬 寃곗젙??媛?ν빐???쒕떎.

### 2026-05-13 SCRFD adapter/?꾪꽣 ?뺥빀 寃利?
肄붾뱶 蹂寃?

- `AutoMaskOptions.FilterProfile`??異붽???FaceONNX? SCRFD ?꾨낫 ?꾪꽣瑜?遺꾨━?덈떎.
- FaceONNX 湲곕낯 profile? 湲곗〈 硫댁쟻/醫낇슒鍮?skin/edge/luma ?꾪꽣 湲곗????좎??쒕떎.
- SCRFD profile? ?곗꽑 raw detector ?덉쭏???뺤씤?섍린 ?꾪빐 skin/edge/luma ?듦퀎 ?꾪꽣瑜??꾧퀬, small ?꾨낫 confidence 湲곗???`0.25`濡???톬??
- `AutoMaskOptions.DumpDetectionDiagnostics`? `[AutoMaskDetectionDump]` 濡쒓렇瑜?異붽???frame蹂?raw ?꾨낫 ?섏? post-filter ?꾨낫 ?? top box 醫뚰몴/area/aspect/confidence瑜??뺤씤?????덇쾶 ?덈떎.
- `ScrfdOnnxDetectorOptions.DumpDebug`, `DebugCandidateLimit`??異붽??덈떎.
- SCRFD debug 濡쒓렇??output tensor ?대쫫/shape, score-box pairing, stride, feature map ?ш린, anchors per point, raw-after-threshold ?꾨낫? NMS ??top box瑜?異쒕젰?쒕떎.
- `scripts/run-srcTest-smoke.ps1`??`-ScrfdDebugDump`, `-ScrfdNoStrideScale`??異붽??덈떎. 湲곗〈 `-ScrfdUseBgr`, `-ScrfdStretchInput`怨??④퍡 RGB/BGR, letterbox/stretch, bbox stride scale true/false瑜?鍮꾧탳?????덈떎.
- smoke harness?먯꽌 SCRFD ?ㅽ뻾 ??`FaceTrackPostProcessOptions`瑜?蹂꾨룄 profile濡???떠 `StrongConfidence=0.55`, `ShortTrackMaxConfidence=0.55`, `MinTrackIou=0.08`, `MaxCenterShiftRatio=0.75`, `MaxAreaChangeRatio=4.0`???곸슜?덈떎.
- 湲곗〈 FaceONNX baseline ?ㅽ뻾?먮뒗 FaceONNX detector, FaceONNX filter profile, 湲곗〈 track ?듭뀡???좎??덈떎.

寃利?clip:

- ?먮낯: `D:\WorkSpace\src\260101_oneday6.mp4`
- clip: `.tmp/srcTest-smoke/oneday6-0030-3s.mp4`
- 怨듯넻 議곌굔: ?먮낯 ?댁긽?? `DetectEveryNFrames=1`, tracking on, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, `ParallelDetectorCount=2`, export ?앸왂
- YuNet? ?대쾲 ?쇱슫?쒖뿉?쒕룄 ?ㅽ뻾?섏? ?딆븯??

FaceONNX baseline:

- `faceMaskFrames=19`
- `[AutoRunSummary]`: `detector=FaceOnnxDetector/CPU`, `processed=90`, `detects=90`, `totalMs=30825~32445`
- filter: `regular=5`, `small=5`, `rejected=0`, `statsRejected=0`
- track 蹂댁젙: `tracks=6`, `filled=8`, `lostFilled=3`, `lostFrames=44,45,46`, `removedShort=2`, `rewritten=19`
- baseline ?쇨뎬 frame? `0,14~20,33~37,41~46` 援ш컙?댁뿀??

SCRFD 500M RGB/letterbox/stride-scale on:

- output: `score_8[1x12800x1]`, `score_16[1x3200x1]`, `score_32[1x800x1]`, `bbox_8[1x12800x4]`, `bbox_16[1x3200x4]`, `bbox_32[1x800x4]`
- pairing/stride: score/bbox count 湲곗? pairing??`8/16/32` stride? `80x80/40x40/20x20`, `anchorsPerPoint=2`濡?留욎븯??
- `[AutoRunSummary]`: `totalMs=4554`, filter `regular=13`, `small=3`, `rejected=1`, `statsRejected=0`
- track 蹂댁젙: `tracks=9`, `filled=14`, `lostFilled=0`, `removedShort=6`, `rewritten=24`
- `faceMaskFrames=24`
- baseline 鍮꾧탳: `baselineFrames=19`, `optimizedFrames=24`, `common=8`, `onlyBaseline=11`, `onlyOptimized=16`, `avgBestIou=0.000`, `minBestIou=0.000`
- raw ?꾨낫??frame 3/4, 11~19, 22~34, 50 ?깆뿉???섏삤吏留?baseline ?쇨뎬 醫뚰몴? 寃뱀튂吏 ?딆븯??
- ?먯젙: output pairing怨?bbox stride decode ?먯껜??援ъ“??留욎븘 蹂댁씤?? ?ㅽ뙣 ?먯씤? FaceONNX??skin/luma ?꾪꽣 ?덈씫???꾨땲?? raw ?꾨낫媛 baseline ?쇨뎬怨??ㅻⅨ ?꾩튂/援ш컙???섏삤??detector/?꾩쿂由??덉쭏 臾몄젣??

SCRFD 500M `MultiplyBboxByStride=false`:

- `[AutoRunSummary]`: `totalMs=5809`, filter `regular=0`, `small=0`, `rejected=24`, `statsRejected=0`
- `faceMaskFrames=0`
- raw top box?ㅼ씠 `areaRatio=0.000003~0.0001` ?섏??쇰줈 吏?섏튂寃??묒븘議뚭퀬 post-filter?먯꽌 ?꾨? ?쒓굅?먮떎.
- ?먯젙: ??紐⑤뜽? bbox distance??stride scale??怨깊빐???쒕떎. `MultiplyBboxByStride=false`???먭린?쒕떎.

SCRFD 500M BGR/letterbox/stride-scale on:

- `[AutoRunSummary]`: `totalMs=5230`, filter `regular=13`, `small=2`, `rejected=4`, `statsRejected=0`
- track 蹂댁젙: `tracks=11`, `filled=13`, `lostFilled=0`, `removedShort=8`, `rewritten=20`
- `faceMaskFrames=20`
- ?쇰? ?꾨낫 ?섎뒗 baseline怨?鍮꾩듂?섏?留?frame 3/4, 44~54, 59~65 ??baseline怨??ㅻⅨ 援ш컙/醫뚰몴媛 以묒떖?댁뿀??
- ?먯젙: BGR ?꾪솚?쇰줈 baseline ?뺥빀???뚮났?섏? ?딆븯??

SCRFD 500M RGB/stretch/stride-scale on:

- `[AutoRunSummary]`: `totalMs=5362`, filter `regular=64`, `small=15`, `rejected=6`, `statsRejected=0`
- track 蹂댁젙: `tracks=15`, `filled=25`, `lostFilled=1`, `removedShort=8`, `rewritten=51`
- `faceMaskFrames=51`
- stretch???꾨낫瑜??ш쾶 ?섎졇吏留??ㅼ닔??optimized-only ?꾨낫媛 ?앷꺼 ?ㅽ깘 ?꾪뿕??而ㅼ죱??
- ?먯젙: stretch ?낅젰? ??clip?먯꽌 ?덉쭏???뚮났?섏? 紐삵븯怨??ㅽ깘???섎졇??

SCRFD 10G RGB/letterbox/stride-scale on:

- output: `448[12800x1]`, `471[3200x1]`, `494[800x1]`, `451[12800x4]`, `474[3200x4]`, `497[800x4]`, keypoint `454/477/500`? last dimension 10?대씪 box pairing?먯꽌 ?쒖쇅?먮떎.
- pairing/stride: score/bbox pairing? `8/16/32` stride? `anchorsPerPoint=2`濡?留욎븯??
- `[AutoRunSummary]`: `totalMs=5234`, filter `regular=13`, `small=15`, `rejected=3`, `statsRejected=0`
- track 蹂댁젙: `tracks=9`, `filled=19`, `lostFilled=0`, `removedShort=5`, `rewritten=42`
- `faceMaskFrames=42`
- baseline 鍮꾧탳: `baselineFrames=19`, `optimizedFrames=42`, `common=10`, `onlyBaseline=9`, `onlyOptimized=32`, `avgBestIou=0.000`, `minBestIou=0.000`
- raw/post-filter ?꾨낫媛 frame 1~7, 14~17, 30~41, 50~63, 75~79 ??baseline怨??ㅻⅨ 援ш컙??吏묒쨷?먮떎.
- ?먯젙: 10G??output decode 援ъ“??留욎븘 蹂댁씠吏留? baseline ?쇨뎬怨?醫뚰몴媛 留욎? ?딅뒗?? ?꾩옱 adapter/?꾩쿂由?議고빀?먯꽌??raw detector ?꾨낫 ?덉쭏 臾몄젣媛 二쇰맂 ?ㅽ뙣 ?먯씤?대떎.

?대쾲 ?쇱슫??寃곕줎:

- FaceONNX baseline? ?좎??쒕떎.
- SCRFD 500M/10G 紐⑤몢 FaceONNX skin/luma ?꾪꽣 ?뚮Ц?먮쭔 ?ㅽ뙣??寃껋? ?꾨땲?? SCRFD profile濡?stats ?꾪꽣瑜??고쉶?대룄 baseline怨?IoU媛 0?닿퀬 optimized-only ?꾨낫媛 留롫떎.
- `PairScoreAndBoxTensors()`???대쾲 ??紐⑤뜽??output shape 湲곗??쇰줈??score/bbox/keypoint瑜??щ컮瑜닿쾶 遺꾨━?덈떎.
- `GuessStride()`? anchors per point 怨꾩궛??500M/10G 紐⑤몢 `8/16/32`, `2 anchors`濡?留욎븯??
- `MultiplyBboxByStride=false`??box媛 吏?섏튂寃??묒븘吏??decode ?ㅻ쪟 寃쎈줈濡??뺤씤?덈떎.
- RGB/BGR, letterbox/stretch 鍮꾧탳?먯꽌 baseline ?뺥빀???뚮났??議고빀? ?놁뿀??
- track ?듭뀡??SCRFD ?꾩슜?쇰줈 ?꾪솕?대룄 raw ?꾨낫 援ш컙/醫뚰몴 ?먯껜媛 ?ㅻⅤ湲??뚮Ц???덉쭏 ?ㅽ뙣瑜??닿껐?섏? 紐삵뻽??

?꾨낫 ?먮떒:

- SCRFD 500M: `蹂대쪟`. 鍮좊Ⅴ怨?adapter 怨꾩륫? ?뺤긽?붾릱吏留? ?꾩옱 紐⑤뜽/?꾩쿂由?議고빀?먯꽌??raw ?꾨낫媛 baseline ?쇨뎬怨?留욎? ?딄퀬 FaceONNX-only/SCRFD-only 李⑥씠媛 ?щ떎. ?ㅻⅨ SCRFD variant, ?낅젰 ?뺢퇋??letterbox 援ы쁽, 紐⑤뜽 異쒖쿂蹂?preprocessing??異붽? ?뺤씤?섍린 ?꾧퉴吏 湲곕낯/accurate ?꾨낫濡??щ━吏 ?딅뒗??
- SCRFD 10G: `?먭린`. 500M蹂대떎 ??紐⑤뜽?몃뜲??媛숈? clip?먯꽌 baseline ?뺥빀???뚮났?섏? ?딄퀬 optimized-only ?꾨낫媛 ??留롫떎. ?꾩옱 CPU/DirectML ?ㅽ뻾?먯꽌??500M ?鍮??꾨낫 ?덉쭏 ?댁젏???뺤씤?섏? ?딆븯??
- 湲곕낯 detector: `FaceONNX ?좎?`. ??clip 湲곗? FaceONNX??baseline/postprocess媛 ?쇨??섍퀬, SCRFD??raw ?꾨낫 ?④퀎?먯꽌 ?ㅽ뙣?쒕떎.

### 2026-05-13 SCRFD preprocessing/decode 異붽? 寃利?
紐⑺몴??SCRFD 理쒖쥌 ?먭린 ?먯껜媛 ?꾨땲??FaceONNX 湲곗? ?꾩쿂由??꾪꽣???듭?濡??ㅼ뼱媛??援ъ“瑜?怨꾩냽 遺꾨━?섎㈃???ㅽ뙣 ?먯씤????醫곹엳??寃껋씠?? ?대쾲 異붽? ?쇱슫?쒖뿉?쒕룄 YuNet? ?ㅽ뻾?섏? ?딆븯??

肄붾뱶 蹂寃?

- `ScrfdOnnxDetectorOptions`??`AnchorCenterOffset`, `CenterLetterboxPadding`, `LetterboxPaddingValue`, `InputMean`, `InputStd`, `InputWidth`, `InputHeight` 湲곕컲 ?낅젰 ?ш린 override瑜?異붽??덈떎.
- smoke script??`-ScrfdHalfStrideAnchor`, `-ScrfdCenterLetterbox`, `-ScrfdInputSize`, `-ScrfdInputMean`, `-ScrfdInputStd`, `-ScrfdPaddingValue`瑜?異붽??덈떎.
- letterbox padding ?곸뿭? ???댁긽 tensor 湲곕낯媛?`0`?쇰줈 諛⑹튂?섏? ?딄퀬 `(paddingValue - mean) / std`濡?梨꾩슫?? 湲곕낯媛믪? InsightFace 怨꾩뿴 ?꾩쿂由ъ뿉 留욎떠 `paddingValue=0`, `mean=127.5`, `std=128`?대떎.
- bbox anchor center??湲곗〈 `(x + 0.5) * stride`? InsightFace??`x * stride`瑜?鍮꾧탳?????덇쾶 遺꾨━?덈떎. 湲곕낯媛믪? `AnchorCenterOffset=0.0`?대떎.
- 10G泥섎읆 ?낅젰 shape媛 ?숈쟻??紐⑤뜽? `-ScrfdInputSize`濡??낅젰 ?ш린瑜?諛붽퓭 ?쒕룄?????덇쾶 ?덈떎. 500M? model metadata媛 `1x3x640x640` 怨좎젙?대씪 640 ???낅젰? ?ъ슜?섏? ?딅뒗??

寃利?clip/怨듯넻 議곌굔:

- clip: `.tmp/srcTest-smoke/oneday6-0030-3s.mp4` (`D:\WorkSpace\src\260101_oneday6.mp4`?먯꽌 留뚮뱺 3珥?clip)
- 怨듯넻 議곌굔: ?먮낯 ?댁긽?? `DetectEveryNFrames=1`, tracking on, `DetectionThreshold=0.2`, `ConfidenceThreshold=0.25`, `NmsThreshold=0.7`, `ParallelDetectorCount=2`, export ?앸왂
- FaceONNX baseline? ?ㅼ떆 `faceMaskFrames=19`, `filter regular=5/small=5/rejected=0/statsRejected=0`, track ??`rewritten=19`濡??뺤씤?먮떎.

SCRFD 500M 異붽? 寃利?

- RGB/letterbox/top-left padding/normalized padding/anchor offset `0.0`: `faceMaskFrames=15`, filter `regular=13`, `small=2`, `rejected=3`, track ??`rewritten=15`. FaceONNX baseline ?鍮?`baselineFrames=19`, `optimizedFrames=15`, `common=2`, `onlyBaseline=17`, `onlyOptimized=13`, `avgBestIou=0.000`, `minBestIou=0.000`.
- 媛숈? 議곌굔?먯꽌 湲곗〈 half-stride anchor offset `0.5`: `faceMaskFrames=15`, filter `regular=12`, `small=2`, `rejected=4`, track ??`rewritten=15`. anchor 湲곗????섎룎?ㅻ룄 baseline ?뺥빀? ?뚮났?섏? ?딆븯??
- center letterbox padding: `faceMaskFrames=47`, filter `regular=31`, `small=13`, `rejected=2`, track ??`rewritten=47`. ?꾨낫媛 ?ш쾶 ?섏뼱 ?ㅽ깘 ?꾪뿕??而ㅼ죱怨?baseline ?뺥빀 媛쒖꽑 ?좏샇???놁뿀??
- mean/std raw ?낅젰(`-ScrfdInputMean 0 -ScrfdInputStd 1`): `faceMaskFrames=46`, filter `regular=50`, `small=5`, `rejected=0`, track ??`rewritten=46`. raw ?꾨낫媛 ?ш쾶 ?섏뼱 ?ㅽ깘 ?꾪뿕??而ㅼ죱怨?baseline ?뺥빀 媛쒖꽑 ?좏샇???놁뿀??
- 寃곕줎: 500M? output pairing/stride/anchor count肉??꾨땲??padding 媛? padding ?꾩튂, anchor center, mean/std瑜?遺꾨━?대룄 raw ?꾨낫媛 ?ㅼ젣 baseline ?쇨뎬 洹쇱쿂濡??덉젙?곸쑝濡??대룞?섏? ?딆븯?? ?ㅻ쭔 ?띾룄? adapter 援ъ“ ?먯껜???좎? 媛移섍? ?덉뼱 `蹂대쪟`濡??붾떎.

SCRFD 10G 異붽? 寃利?

- RGB/letterbox/top-left padding/normalized padding/anchor offset `0.0`: output? `448/471/494` score, `451/474/497` bbox, `454/477/500` keypoint濡?遺꾨━?먭퀬 score/bbox??stride `8/16/32`, anchors per point `2`濡??뺥빀?먮떎. `faceMaskFrames=47`, filter `regular=13`, `small=15`, `rejected=2`, track ??`rewritten=47`.
- FaceONNX baseline ?鍮?`baselineFrames=19`, `optimizedFrames=47`, `common=10`, `onlyBaseline=9`, `onlyOptimized=37`, `avgBestIou=0.000`, `minBestIou=0.000`.
- center letterbox padding: `faceMaskFrames=36`, filter `regular=16`, `small=10`, `rejected=2`, track ??`rewritten=36`. ?꾨낫 ?섎뒗 以꾩뿀吏留?baseline ?뺥빀???뚮났?덈떎???좏샇???놁뿀??
- `-ScrfdInputSize 320`: 10G model metadata???숈쟻 ?낅젰泥섎읆 蹂댁씠??DirectML ?ㅽ뻾?먯꽌 `Reshape_223` ?ㅻ쪟媛 諛쒖깮????吏꾪뻾??硫덉톬?? ?곕씪???꾩옱 DML ?ㅽ뻾 寃쎈줈?먯꽌??640 ???낅젰 ?ш린 寃利앹? ?ㅽ뙣 寃쎈줈濡?湲곕줉?쒕떎.
- 寃곕줎: 10G??500M蹂대떎 ??紐⑤뜽?댁?留?媛숈? clip?먯꽌 raw/post-filter ?꾨낫媛 baseline ?쇨뎬怨?留욎? ?딄퀬 optimized-only媛 留롫떎. ?낅젰 ?ш린 蹂寃쎈룄 ?꾩옱 ?ㅽ뻾 寃쎈줈?먯꽌 ?덉젙?곸쑝濡?寃利앸릺吏 ?딆븯?? ?곕씪??10G??`?먭린` ?먮떒???좎??쒕떎.

?꾩옱 ?먮떒:

- `FaceONNX`: 湲곕낯 detector ?좎?. ??clip?먯꽌 baseline frame/postprocess媛 ?덉젙?곸씠??
- `SCRFD 500M`: `蹂대쪟`. ?꾩쿂由ъ? bbox decode 異뺤쓣 ??遺꾨━?대룄 ?뺥빀???뚮났?섏? ?딆븯吏留? 紐⑤뜽??鍮좊Ⅴ怨?adapter ?ㅽ뿕 湲곕컲? ?④만 媛移섍? ?덈떎. ?ㅻⅨ SCRFD variant??紐⑤뜽 異쒖쿂蹂??꾩쿂由?洹쇨굅媛 異붽????뚮쭔 ?ш??좏븳??
- `SCRFD 10G`: `?먭린`. 500M ?鍮??덉쭏 ?댁젏???녾퀬 optimized-only ?꾨낫媛 留롮쑝硫? ?숈쟻 input size 蹂寃쎈룄 ?꾩옱 DML 寃쎈줈?먯꽌 ?ㅽ뙣?덈떎.

## 2026-05-22 YOLO backend 遺꾨━ 援ы쁽 紐⑺몴

?묒뾽 釉뚮옖移? `feature/yolo-auto-mosaic-backend`

?대쾲 ?쇱슫?쒖쓽 紐⑺몴??FaceONNX瑜??쒓굅?섍굅??湲곗〈 理쒖쟻?붽컪????뼱?곕뒗 寃껋씠 ?꾨땲?? ?꾩옱 寃利앸맂 FaceONNX ?덉쭏/?띾룄 ?ㅼ젙? 洹몃?濡?蹂댁〈?섍퀬, YOLO 怨꾩뿴 detector瑜?蹂꾨룄 backend/profile濡?異붽???媛숈? ?먮룞 紐⑥옄?댄겕 ?뚯씠?꾨씪?몄뿉???좏깮 ?ㅽ뻾?????덇쾶 留뚮뱺??

紐⑺몴 臾몄옣:

```text
AUTO_MOSAIC_QUALITY_SPEED_PLAN.md:1 ?댁슜??湲곗??쇰줈 FaceShield ?먮룞 紐⑥옄?댄겕 ?뚯씠?꾨씪?몄뿉 YOLO 湲곕컲 detector backend瑜?異붽??섍퀬, 湲곗〈 FaceONNX 理쒖쟻???ㅼ젙媛믨낵 ?숈옉? 洹몃?濡??좎???梨?FaceONNX? YOLO瑜?紐⑤뜽蹂꾨줈 ?좏깮???ъ슜?????덈룄濡?援ы쁽?쒕떎. FaceONNX??threshold/filter/track/ROI/auto-tune ?ㅼ젙? 湲곗〈 寃利앷컪???쇱넀?섏? ?딄퀬 蹂댁〈?섎ŉ, YOLOv8-Face ?먮뒗 YOLO5Face ONNX ?꾨낫?먮뒗 YOLO ?꾩슜 threshold, NMS, ?꾨낫 ?꾪꽣, small-face 泥섎━, track ?꾩쿂由? ROI ?ш?異? auto-tune/profile ?ㅼ젙??蹂꾨룄濡?遺꾨━??理쒖쟻?뷀븳?? ?ъ슜?먮뒗 FaceONNX? YOLO 紐⑤뜽???좏깮???먮룞 紐⑥옄?댄겕瑜??ㅽ뻾?????덉뼱???섎ŉ, 媛?紐⑤뜽? ?쒕줈 ?ㅻⅨ 理쒖쟻??媛믪쓣 ?낅┰?곸쑝濡?媛?몄빞 ?쒕떎. srcTest ???援ш컙?먯꽌 FaceONNX baseline怨?YOLO ?꾨낫瑜?A/B 鍮꾧탳?섍퀬, YOLO媛 ?ㅼ젣 誘명깘/?ㅽ깘/源쒕컯??諛뺤뒪 ???덉쭏???좎??섍굅??媛쒖꽑?섎㈃???먮룞 寃異?totalMs ?먮뒗 export totalMs瑜?以꾩씠?붿? 寃利앺븳?? ?덉쭏 gate瑜??듦낵??YOLO ?ㅼ젙留?異붿쿇 ?꾨낫濡?臾몄꽌?뷀븯怨? ?ㅽ뙣??YOLO ?ㅼ젙? ?먯씤怨?蹂대쪟/?먭린 ?먮떒??AUTO_MOSAIC_QUALITY_SPEED_PLAN.md??湲곕줉?쒕떎. ??紐⑺몴?먮뒗 branch ?앹꽦, commit, push, pull, reset, stash 媛숈? git ?묒뾽? ?ы븿?섏? ?딅뒗?? git 愿???묒뾽? 蹂꾨룄 ?ъ슜??吏?쒓? ?덉쓣 ?뚮쭔 ?섑뻾?쒕떎. 洹???紐⑺몴 踰붿쐞 ?덉쓽 肄붾뱶 援ы쁽, 臾몄꽌 ?섏젙, 濡쒖뺄 鍮뚮뱶, smoke ?ㅽ뻾, 紐⑤뜽 ?꾨낫 ?ㅼ슫濡쒕뱶/寃利? A/B ?뚯뒪?? ?붾쾭洹?濡쒓렇 異붽?, threshold/profile ?쒕떇? ?ъ슜?먯뿉寃?留ㅻ쾲 ?뺤씤?섏? ?딄퀬 ?먯쑉?곸쑝濡?吏꾪뻾?쒕떎.
```

?듭떖 議곌굔:

- ??紐⑺몴 踰붿쐞?먯꽌 git ?묒뾽? ?쒖쇅?쒕떎. branch ?앹꽦, commit, push, pull, reset, stash ?깆? 蹂꾨룄 吏?쒓? ?덉쓣 ?뚮쭔 ?섑뻾?쒕떎.
- git???쒖쇅??援ы쁽/?섏젙/濡쒖뺄 寃利?紐⑤뜽 ?꾨낫 ?ㅽ뿕? 留??④퀎 ?뱀씤 ?붿껌 ?놁씠 ?먯쑉?곸쑝濡?吏꾪뻾?쒕떎.
- FaceONNX 湲곗〈 湲곕낯媛믨낵 寃利앷컪? 蹂寃쏀븯吏 ?딅뒗??
- FaceONNX? YOLO??媛숈? ?ㅼ젙 媛앹껜瑜?怨듭쑀?섏? ?딄퀬 detector蹂?profile??媛吏꾨떎.
- ?ъ슜?먮뒗 FaceONNX? YOLO 紐⑤뜽???좏깮?댁꽌 ?먮룞 紐⑥옄?댄겕瑜??ㅽ뻾?????덉뼱???쒕떎.
- YOLO threshold, NMS, ?꾨낫 ?꾪꽣, small-face 湲곗?, track ?꾩쿂由? ROI ?ш?異? auto-tune ?꾨낫??YOLO ?꾩슜 媛믪쑝濡?遺꾨━?쒕떎.
- YOLO媛 FaceONNX蹂대떎 鍮좊Ⅴ?붾씪???ㅼ젣 誘명깘, ?ㅼ젣 ?ㅽ깘, 源쒕컯?? 諛뺤뒪 ?먯씠 ?섎㈃ 湲곕낯媛믪쑝濡??밴꺽?섏? ?딅뒗??
- YOLO媛 ?덉쭏 gate瑜??듦낵?섏? 紐삵븯硫??ㅽ뙣 ?먯씤??紐⑤뜽, decode, ?꾩쿂由? post-filter, track/ROI 以??대뵒?몄? 遺꾨━?댁꽌 湲곕줉?쒕떎.

?곗꽑 寃???꾨낫:

1. `YOLOv8-Face`
   - `nano`???띾룄 ?꾨낫濡?蹂몃떎.
   - `medium`? ?덉쭏 ?꾨낫濡?蹂몃떎.
   - ?⑥젏: 怨듦컻 援ы쁽/weight???쇱씠?좎뒪媛 GPL/Ultralytics 怨꾩뿴?????덉쑝誘濡?諛고룷 ???뺤씤???꾩슂?섎떎.

2. `YOLO5Face`
   - ?묒? ?쇨뎬怨??대젮??媛곷룄 ????꾨낫濡?蹂몃떎.
   - `n/s` 怨꾩뿴遺??ONNX runtime adapter瑜?遺숈뿬 ?띾룄? ?덉쭏???뺤씤?쒕떎.
   - ?⑥젏: 紐⑤뜽 ?뚯씪 異쒖쿂? ?쇱씠?좎뒪 ?뺤씤???꾩슂?섎떎.

珥덇린 援ы쁽 諛⑺뼢:

- `FaceDetectorBackend`??YOLO 怨꾩뿴 backend瑜?異붽??쒕떎.
- `YoloFaceOnnxDetectorOptions`? `YoloFaceOnnxDetector`瑜??덈줈 異붽??쒕떎.
- YOLO output decode??紐⑤뜽蹂?output shape瑜?癒쇱? inspect?섍퀬, anchor-free/anchor-based 援ъ“瑜?紐낇솗??遺꾨━?쒕떎.
- `FaceDetectorFactoryOptions`??FaceONNX, SCRFD, YuNet, YOLO option???낅┰?곸쑝濡?媛吏꾨떎.
- `AutoMaskOptions.FilterProfile`??YOLO profile??異붽??섍퀬 FaceONNX/SCRFD/YuNet怨?遺꾨━?쒕떎.
- smoke script??`-YoloModelPath`, `-YoloModelType`, `-YoloInputSize`, `-YoloConfidenceThreshold`, `-YoloNmsThreshold`, `-YoloDebugDump` ?듭뀡??異붽??쒕떎.
- A/B 鍮꾧탳??FaceONNX baseline??湲곗??쇰줈 `faceMaskFrames`, `onlyBaseline`, `onlyOptimized`, `avgBestIou`, `minBestIou`, track/ROI 濡쒓렇瑜?媛숈씠 蹂몃떎.

寃利?湲곗?:

- 湲곕낯 FaceONNX gate??湲곗〈怨??숈씪?섍쾶 ?듦낵?댁빞 ?쒕떎.
- YOLO ?꾨낫??媛숈? clip?먯꽌 FaceONNX baseline ?鍮?frame ?꾨씫/異붽?, 諛뺤뒪 ??李⑥씠, ??? IoU媛 ?묒븘???쒕떎.
- `onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou`???ㅼ젣 ?ㅽ깘/誘명깘 ?먯젙???꾨땲??baseline-diff ?좏샇濡?湲곕줉?쒕떎.
- ?ㅼ젣 誘명깘/?ㅽ깘? representative overlay ?≪븞 ?뺤씤 ?먮뒗 GT ?쇰꺼 湲곗??쇰줈留??뺤젙?쒕떎.
- YOLO???먮룞 寃異?wall-clock? `[AutoRunSummary].totalMs`濡??먮떒?쒕떎. 蹂묐젹 thread ?꾩쟻媛믪씤 `detectMs`留뚯쑝濡?鍮좊Ⅴ?ㅺ퀬 ?먮떒?섏? ?딅뒗??
- ???clip gate瑜??듦낵??YOLO ?꾨낫留?30珥??댁긽 援ш컙怨?export smoke濡??뺤옣?쒕떎.
- ?꾩껜 紐⑺몴 ?꾨즺 ?꾩뿉??Avalonia GUI?먯꽌 detector ?좏깮, ?먮룞 寃異? preview, export ?먮쫫???뺤씤?댁빞 ?쒕떎.

## 2026-05-22 YOLO backend 1李?援ы쁽 諛?YOLOv8n ?꾨낫 smoke

援ы쁽 ?곹깭:

- `FaceDetectorBackend.YoloFaceOnnx`瑜?異붽??덈떎.
- `YoloFaceOnnxDetectorOptions`, `YoloFaceModelType`, `YoloFaceOnnxDetector`瑜?異붽??덈떎.
- `YoloFaceOnnxDetector`??ONNX Runtime?쇰줈 YOLOv8-Face/YOLO5Face 怨꾩뿴 ?꾨낫瑜??ㅽ뻾?섍퀬, `[1,N,F]` ?먮뒗 `[1,F,N]` ?뺥깭??YOLO ?꾨낫 ?먯꽌瑜??댁꽍?쒕떎. YOLO5Face??raw 3-scale feature map `[1,48,H,W]` 異쒕젰? 蹂꾨룄 decode 寃쎈줈?먯꽌 泥섎━?쒕떎.
- `FaceDetectorFactoryOptions`??FaceONNX/SCRFD/YuNet/YOLO option??蹂꾨룄 ?꾨줈?쇳떚濡?媛吏꾨떎.
- `AutoMaskOptions.FilterProfile`??`Yolo`瑜?異붽??덇퀬, YOLO ?꾨낫 ?꾪꽣??FaceONNX? 遺꾨━?덈떎.
- `WorkspaceViewModel`???먮룞 ?ㅽ뻾 寃쎈줈??FaceONNX backend?먯꽌留?湲곗〈 `DetectorAutoTuner`瑜??곸슜?섍퀬, YOLO backend?먯꽌??YOLO factory/options瑜??좎??쒕떎.
- ???먮룞 ?듭뀡 UI?먯꽌 `FaceONNX`? `YOLO Face ONNX`瑜??좏깮?????덇쾶 ?덈떎. YOLO ?좏깮 ??YOLO 紐⑤뜽 醫낅쪟, `.onnx` 寃쎈줈, ?낅젰 ?ш린, objectness/confidence/NMS 媛믪쓣 蹂꾨룄 ?낅젰?쒕떎.
- `scripts/run-srcTest-smoke.ps1`??`-YoloModelPath`, `-YoloModelType`, `-YoloInputSize`, `-YoloObjectnessThreshold`, `-YoloConfidenceThreshold`, `-YoloNmsThreshold`, `-YoloDebugDump` ?듭뀡??異붽??덈떎. ?댄썑 吏꾨떒?⑹쑝濡?`-DumpCompareDetails`, `-YoloUseFaceOnnxRoiRefine`, `-YoloFaceOnnxRoiMinAreaRatio`, `-YoloFaceOnnxRoiMaxCandidates`??異붽??덈떎.

FaceONNX ?뚭? 寃利?

- `dotnet build FaceShield.sln` ?깃났.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾 ?깃났.
- 湲곕낯 verifier??quality gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- 媛숈? verifier?먯꽌 ROI-hit ???援ш컙? `attempts=11`, `hits=5`瑜??좎??덈떎.
- auto-tune short gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=53,703ms`濡??듦낵?덈떎. ?꾩옱 ?λ퉬/遺?섏뿉?쒕뒗 ?쒕꼫媛 CPU 2?몄뀡???좏깮?덈떎.

YOLO ?꾨낫:

- ?ㅼ슫濡쒕뱶 ?꾨낫: `lindevs/yolov8-face` release `1.0.1`??`yolov8n-face-lindevs.onnx`.
- 濡쒖뺄 寃쎈줈: `.tmp/models/yolov8n-face-lindevs.onnx`.
- SHA-256: `8d0bfb0c3383c5bd7a78dd24ef79a21e2aa456619b6ab5e53867092d1c7dc414`.
- 紐⑤뜽 metadata: input `images[1x3x640x640]`, output `output0[1x5x8400]`.
- ?쇱씠?좎뒪/諛고룷 ?곹빀?깆? ?꾩쭅 ?뺤젙?섏? ?딆븯?? ?꾨낫 ?ㅽ뿕??濡쒖뺄 ?뚯씪濡쒕쭔 痍④툒?쒕떎.

YOLOv8n 湲곕낯 threshold smoke:

- 紐낅졊:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.25 -YoloConfidenceThreshold 0.35 -YoloNmsThreshold 0.45 -YoloDebugDump
```

- adapter ?ㅽ뻾? ?깃났?덈떎.
- YOLO optimized `totalMs=14,655ms`濡?FaceONNX baseline `totalMs=62,090ms`蹂대떎 鍮⑤옄??
- ?섏?留?YOLO???꾩쿂由???`faceMaskFrames=0`?댁뿀??
- A/B 寃곌낵??`baselineFrames=19`, `optimizedFrames=0`, `onlyBaseline=19`, `onlyOptimized=0`, `avgBestIou=0.000`, `minBestIou=0.000`, `passed=False`.
- ?먮떒: `?먭린`. ??threshold???뚮젮吏??쇨뎬 援ш컙?먯꽌 理쒖쥌 留덉뒪?ш? 0?꾨젅?꾩씠??quality gate瑜??듦낵?섏? 紐삵븳??

YOLOv8n low-threshold smoke:

- 紐낅졊:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.05 -YoloConfidenceThreshold 0.05 -YoloNmsThreshold 0.45
```

- YOLO optimized `totalMs=13,033ms`濡?FaceONNX baseline `totalMs=59,679ms`蹂대떎 鍮⑤옄??
- ?꾨낫???앷꼈吏留??꾩쿂由???`faceMaskFrames=8`??洹몄낀??
- A/B 寃곌낵??`baselineFrames=19`, `optimizedFrames=8`, `common=8`, `onlyBaseline=11`, `onlyOptimized=0`, `avgBestIou=0.603`, `minBestIou=0.048`, `boxCountDiffFrames=7`, `passed=False`.
- ?먮떒: `蹂대쪟`. ?띾룄???좎쓽誘명븯寃?鍮좊Ⅴ吏留?誘명깘怨?諛뺤뒪 ?뺥빀 ?ㅽ뙣媛 而ㅼ꽌 異붿쿇 ?꾨낫媛 ?꾨땲?? ?ㅽ뙣 異뺤? 紐⑤뜽 ?꾨낫???묒? ?쇨뎬/?λ㈃ ?곹빀?? 640 怨좎젙 ?낅젰 ?댁긽?? low-threshold ?꾨낫???꾩튂 ?뺥빀, YOLO ?꾩슜 ?꾩쿂由?紐⑤몢??嫄몄퀜 ?덈떎. ?꾩옱 利앷굅留뚯쑝濡쒕뒗 decode ?먯껜 ?ㅽ뙣?쇨퀬 ?⑥젙?섏? ?딅뒗??

?꾩옱 異붿쿇 ?꾨낫:

- ?놁쓬.

?ㅼ쓬 ?꾨낫:

- 媛숈? adapter濡??ㅻⅨ YOLOv8-Face ?꾨낫瑜?鍮꾧탳?쒕떎. ?곗꽑?쒖쐞???????낅젰 ?먮뒗 ???믪? mAP ?꾨낫吏留? ?꾩옱 紐⑤뜽? metadata媛 `1x3x640x640` 怨좎젙?대씪 ?⑥닚 `-YoloInputSize 1280` ?ㅽ뿕? 癒쇱? 紐⑤뜽 ?숈쟻 ?낅젰 ?щ?瑜??뺤씤????吏꾪뻾?쒕떎.
- YOLO5Face ONNX ?꾨낫瑜??뺣낫??`Yolo5Face` decode 寃쎈줈瑜?寃利앺븳??
- YOLO媛 3珥?gate瑜??듦낵?섍린 ?꾩뿉??30珥?export smoke濡??밴꺽?섏? ?딅뒗??

## 2026-05-22 YOLO tiling ?ㅽ뿕

援ы쁽 ?곹깭:

- `YoloFaceOnnxDetectorOptions`??`UseTiling`, `IncludeFullFrameWhenTiling`, `TileColumns`, `TileRows`, `TileOverlapRatio`瑜?異붽??덈떎.
- `YoloFaceOnnxDetector`???꾩껜 ?꾨젅???⑥씪 異붾줎 ?몄뿉 source frame??寃뱀튂??tile region?쇰줈 ?섎닠 YOLO瑜??ㅽ뻾?????덈떎.
- ???먮룞 ?듭뀡 UI??YOLO ?꾩슜 ???寃異? ??쇰쭔 ?ㅽ뻾, ??????? ???寃뱀묠 媛믪쓣 異붽??덈떎.
- `scripts/run-srcTest-smoke.ps1`??`-YoloUseTiling`, `-YoloTileOnly`, `-YoloTileColumns`, `-YoloTileRows`, `-YoloTileOverlapRatio`瑜?異붽??덈떎.

?ㅽ뿕 1: YOLOv8n low-threshold + 2x2 tile-only

- 紐낅졊:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.05 -YoloConfidenceThreshold 0.05 -YoloNmsThreshold 0.45 -YoloUseTiling -YoloTileOnly -YoloTileColumns 2 -YoloTileRows 2 -YoloTileOverlapRatio 0.15
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=58,923ms`.
- YOLO tile-only: `optimizedFrames=16`, `totalMs=46,520ms`.
- A/B 寃곌낵: `common=9`, `onlyBaseline=10`, `onlyOptimized=7`, `avgBestIou=0.741`, `minBestIou=0.572`, `boxCountDiffFrames=8`, `passed=False`.
- ?먮떒: `蹂대쪟`. tiling? FaceONNX-only frame???쇰? 以꾩?吏留?YOLO-only/遺덉씪移?frame???앷꼈怨?IoU gate瑜??듦낵?섏? 紐삵뻽?? ?띾룄??non-tiled YOLO蹂대떎 ?ш쾶 ?먮젮議뚮떎.

?ㅽ뿕 2: YOLOv8n low-threshold + full-frame + 2x2 tile

- 紐낅졊:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipBaseline -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/yolov8n-face-lindevs.onnx -YoloModelType YoloV8Face -YoloInputSize 640 -YoloObjectnessThreshold 0.05 -YoloConfidenceThreshold 0.05 -YoloNmsThreshold 0.45 -YoloUseTiling -YoloTileColumns 2 -YoloTileRows 2 -YoloTileOverlapRatio 0.15
```

- YOLO full+tile: `faceMaskFrames=23`, `totalMs=61,397ms`, ROI refine `attempts=8`, `hits=1`.
- ???ㅽ뻾? 鍮좊Ⅸ 吏꾨떒?⑹쑝濡?baseline compare瑜??앸왂?덈떎.
- ?먮떒: `蹂대쪟`. frame ?섎뒗 baseline 19蹂대떎 留롮븘議뚯?留? 吏곸쟾 tile-only A/B?먯꽌 ?대? `onlyOptimized`媛 諛쒖깮?덇퀬 full+tile? `totalMs`媛 FaceONNX baseline ?섏?源뚯? ?щ씪?붾떎. ?곕씪??異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??

?꾩옱 YOLOv8n ?뺣━:

- non-tiled low-threshold: 鍮좊Ⅴ吏留?`optimizedFrames=8`, IoU ?ㅽ뙣.
- tile-only low-threshold: 誘명깘? 以꾩뿀吏留?`onlyBaseline=10`, `onlyOptimized=7`, IoU ?ㅽ뙣.
- full+tile low-threshold: ?꾨낫 ?섎뒗 ?섏뿀吏留??띾룄 ?댁젏??嫄곗쓽 ?щ씪吏怨??ㅽ깘 媛?μ꽦??而ㅼ죱??
- ?곕씪??`yolov8n-face-lindevs.onnx`???꾩옱 `蹂대쪟` ?좎?. 異붿쿇 ?꾨낫 ?놁쓬.

9遺?2珥?異붽? gate:

- `YoloObjectnessThreshold=0.05`, `YoloConfidenceThreshold=0.05`, `YoloNmsThreshold=0.45` low-threshold 議고빀??`.tmp/srcTest-smoke/smoke-0900-2s.mp4`?먯꽌 ?ш?利앺뻽??
- FaceONNX baseline: `baselineFrames=55`, `totalMs=24,280ms`.
- YOLOv8n optimized: `optimizedFrames=62`, `totalMs=12,401ms`.
- A/B 寃곌낵: `common=55`, `onlyBaseline=0`, `onlyOptimized=7`, `avgBestIou=0.731`, `minBestIou=0.488`, `boxCountDiffFrames=39`, `passed=False`.
- 異붽? frame? `0,1,2,3,4,8,9`?怨? low-threshold ?뱀꽦???붾㈃ ?곷떒 ?묒? ?쇨뎬 ?꾨낫 ?몄뿉 ?섎떒/以묐떒 臾쇱껜???꾨낫??媛숈씠 ?ㅼ뼱?붾떎.
- `YoloObjectnessThreshold=0.20`, `YoloConfidenceThreshold=0.20`, `YoloNmsThreshold=0.45` 以묎컙 threshold???뺤씤?덈떎.
- FaceONNX baseline: `baselineFrames=55`, `totalMs=24,099ms`.
- YOLOv8n optimized: `optimizedFrames=57`, `totalMs=11,254ms`.
- A/B 寃곌낵: `common=53`, `onlyBaseline=2`, `onlyOptimized=4`, `avgBestIou=0.720`, `minBestIou=0.000`, `boxCountDiffFrames=27`, `passed=False`.
- ?먮떒: 9遺?援ш컙?먯꽌 YOLOv8n? threshold瑜???텛硫?YOLO-only ?꾨낫媛 留롪퀬, threshold瑜??щ━硫??쇰? FaceONNX-only frame???앷릿?? ???쇨뎬 諛뺤뒪 ?뺥빀??FaceONNX 湲곗? gate瑜??섏? 紐삵븳?? ?ㅼ젣 ?ㅽ깘/誘명깘 ?щ???overlay ?먮뒗 GT ?뺤씤???꾩슂?섏?留? ?꾩옱 baseline-diff gate 湲곗??쇰줈??異붿쿇 ?꾨낫媛 ?꾨땲??

FaceONNX ?뚭? ?ш?利?

- YOLO tiling 援ы쁽 ??`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾???ㅼ떆 ?듦낵?덈떎.
- quality gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- ROI-hit ???援ш컙? `attempts=11`, `hits=5`???
- auto-tune short gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=50,439ms`濡??듦낵?덈떎.

## 2026-05-22 YOLOv8m ?泥?紐⑤뜽 smoke

?꾨낫:

- ?ㅼ슫濡쒕뱶 ?꾨낫: `lindevs/yolov8-face` release `1.0.1`??`yolov8m-face-lindevs.onnx`.
- 濡쒖뺄 寃쎈줈: `.tmp/models/yolov8m-face-lindevs.onnx`.
- 紐⑤뜽 ?뚯씪 ?ш린: ??`99MB`.
- `scripts/inspect-onnx-outputs.ps1` 寃곌낵 input? `images=1x3x640x640`, output? `output0=1x5x8400`?댁뿀?? 湲곗〈 YOLOv8n怨?媛숈? generic YOLOv8 face decode 寃쎈줈瑜??ъ슜?쒕떎.

?ㅽ뿕 1: YOLOv8m low threshold `objectness=0.05`, `confidence=0.05`, `nms=0.45`

- ??? `.tmp/srcTest-smoke/smoke-0600-3s.mp4`
- FaceONNX baseline: `baselineFrames=19`, `totalMs=38,256ms`.
- YOLOv8m optimized: `optimizedFrames=29`, `totalMs=34,862ms`.
- A/B 寃곌낵: `common=16`, `onlyBaseline=3`, `onlyOptimized=13`, `avgBestIou=0.406`, `minBestIou=0.000`, `boxCountDiffFrames=9`.
- ?먮떒: low threshold??YOLO-only frame???ш쾶 ?섍퀬 IoU媛 ??븘 3珥?strict gate瑜??듦낵?섏? 紐삵븳?? YOLOv8m? YOLOv8n蹂대떎 ??紐⑤뜽?댁?留???threshold?먯꽌??湲곗〈 FaceONNX ?숈옉怨쇱쓽 李⑥씠媛 ???щ떎.

?ㅽ뿕 2: YOLOv8m middle threshold `objectness=0.20`, `confidence=0.20`, `nms=0.45`

- ??? `.tmp/srcTest-smoke/smoke-0600-3s.mp4`
- FaceONNX baseline: `baselineFrames=19`, `totalMs=38,943ms`.
- YOLOv8m optimized: `optimizedFrames=11`, `totalMs=36,154ms`.
- A/B 寃곌낵: `common=10`, `onlyBaseline=9`, `onlyOptimized=1`, `avgBestIou=0.674`, `minBestIou=0.000`, `boxCountDiffFrames=1`.
- ?먮떒: threshold瑜??щ━硫?YOLO-only frame? 以꾩?留?FaceONNX-only frame???ш쾶 ?섏뼱 recall??遺議깊빐吏꾨떎. ??紐⑤뜽??6遺?3珥????gate 湲곗? 異붿쿇 ?꾨낫媛 ?꾨땲??

YOLOv8m ?꾩옱 ?먮떒:

- 媛숈? adapter?먯꽌 紐⑤뜽 濡쒕뱶? output decode??媛?ν븯??
- 洹몃윭??3珥?gate?먯꽌 low threshold??怨쇨?異???? IoU, middle threshold??FaceONNX ?鍮??꾨씫?쇰줈 ?ㅽ뙣?덈떎.
- 3珥?gate瑜??듦낵?섏? 紐삵뻽?쇰?濡?30珥??댁긽 援ш컙怨?export smoke濡??뺤옣?섏? ?딅뒗??
- ?ㅽ뙣 異뺤? decode 遺덈뒫蹂대떎??紐⑤뜽/threshold curve? YOLOv8 怨꾩뿴 ?꾨낫??post-filter ?뺥빀 臾몄젣??媛源앸떎.

## 2026-05-22 YOLO5Face feature-map decode 諛?smoke

援ы쁽 ?곹깭:

- `YoloFaceOnnxDetector`??YOLO5Face ?꾩슜 raw feature-map decode瑜?異붽??덈떎.
- ?곸슜 ???output shape??`pred0[1x48x80x80]`, `pred1[1x48x40x40]`, `pred2[1x48x20x20]` 媛숈? 3-scale NCHW tensor??
- anchors??`deepcam-cn/yolov5-face`??`models/yolov5s.yaml` ?ㅼ젙??湲곗??쇰줈 ?덈떎: P3/8 `[4,5, 8,10, 13,16]`, P4/16 `[23,29, 43,55, 73,105]`, P5/32 `[146,217, 231,300, 335,433]`.
- decode??YOLO5Face `Detect.forward`??inference 怨듭떇???곕Ⅸ?? `xy=(sigmoid(xy)*2-0.5+grid)*stride`, `wh=(sigmoid(wh)*2)^2*anchor`, `score=sigmoid(objectness)*sigmoid(class)`濡??꾨낫瑜?留뚮뱺 ??letterbox padding/scale???섎룎由곕떎.
- 湲곗〈 generic `[1,F,N]` 寃쎈줈媛 `[1,48,H,W]`瑜??섎せ ?꾨낫 tensor濡??댁꽍?섎뜕 臾몄젣瑜??쇳븯湲??꾪빐 YOLO5Face 4D feature-map 寃쎈줈瑜?癒쇱? 寃?ы븳??

YOLO5Face ?꾨낫:

- ?ㅼ슫濡쒕뱶 ?꾨낫: Hugging Face `hayashiLin/deepfacelivemodels`??`YoloV5Face.onnx`.
- 濡쒖뺄 寃쎈줈: `.tmp/models/YoloV5Face.onnx`.
- SHA-256: `907c295f79eba1b0f4be59bcf5d8aabe4e2a9002ec44c5d1c518b97eb9fb13da`.
- Hugging Face ?섏씠吏 湲곗? license??`GPL-3.0`?쇰줈 ?쒖떆?섏뼱 ?덈떎. ?꾩옱??濡쒖뺄 ?ㅽ뿕 ?꾨낫濡쒕쭔 痍④툒?섍퀬 諛고룷 ?꾨낫濡?異붿쿇?섏? ?딅뒗??

?ㅽ뿕 1: YOLO5Face 湲곕낯 threshold

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.25 -YoloConfidenceThreshold 0.25 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=58,029ms`.
- YOLO5Face optimized: `optimizedFrames=9`, `totalMs=16,116ms`.
- A/B 寃곌낵: `common=9`, `onlyBaseline=10`, `onlyOptimized=0`, `avgBestIou=0.973`, `minBestIou=0.953`, `boxCountDiffFrames=0`, `passed=False`.
- ?먮떒: `蹂대쪟`. decode? 諛뺤뒪 ?뺥빀? ?뺤긽??媛源앹?留?recall??遺議깊빐 ?덉쭏 gate瑜??듦낵?섏? 紐삵븳??

?ㅽ뿕 2: YOLO5Face low threshold

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.10 -YoloConfidenceThreshold 0.10 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=67,228ms`.
- YOLO5Face optimized: `optimizedFrames=23`, `totalMs=22,355ms`.
- A/B 寃곌낵: `common=18`, `onlyBaseline=1`, `onlyOptimized=5`, `avgBestIou=0.755`, `minBestIou=0.000`, `boxCountDiffFrames=5`, `passed=False`.
- ?먮떒: `蹂대쪟`. threshold瑜???텛硫?FaceONNX-only frame? 以꾩?留?YOLO-only/?꾨젅??遺덉씪移섍? ?앷릿??

?ㅽ뿕 3: YOLO5Face 以묎컙 threshold

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.15 -YoloConfidenceThreshold 0.15 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=57,546ms`.
- YOLO5Face optimized: `optimizedFrames=20`, `totalMs=18,010ms`.
- A/B 寃곌낵: `common=18`, `onlyBaseline=1`, `onlyOptimized=2`, `avgBestIou=0.724`, `minBestIou=0.000`, `boxCountDiffFrames=2`, `passed=False`.
- ?먮떒: `蹂대쪟`. 湲곕낯 threshold蹂대떎 recall? 醫뗭븘議뚯?留?gate 湲곗???frame set怨?IoU瑜?留뚯”?섏? 紐삵븳??

?ㅽ뿕 4: YOLO5Face 湲곕낯 threshold + 2x2 tile-only

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.25 -YoloConfidenceThreshold 0.25 -YoloNmsThreshold 0.45 -YoloUseTiling -YoloTileOnly -YoloTileColumns 2 -YoloTileRows 2 -YoloTileOverlapRatio 0.15
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=53,496ms`.
- YOLO5Face tile-only: `optimizedFrames=39`, `totalMs=56,705ms`.
- A/B 寃곌낵: `common=10`, `onlyBaseline=9`, `onlyOptimized=29`, `avgBestIou=0.778`, `minBestIou=0.620`, `boxCountDiffFrames=2`, `passed=False`.
- ?먮떒: `?먭린`. tiling? YOLO-only ?꾨낫瑜??ш쾶 ?섎━怨??띾룄 ?댁젏???щ씪吏꾨떎.

?ㅽ뿕 5: YOLO5Face 洹쇱젒 threshold `objectness=0.12`, `confidence=0.18`

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-srcTest-smoke.ps1 -SkipTrim -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipExport -OptimizedCpuOnly -YoloModelPath .tmp/models/YoloV5Face.onnx -YoloModelType Yolo5Face -YoloInputSize 640 -YoloObjectnessThreshold 0.12 -YoloConfidenceThreshold 0.18 -YoloNmsThreshold 0.45
```

- FaceONNX baseline: `baselineFrames=19`, `totalMs=76,625ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=17,580ms`.
- A/B 寃곌낵: `common=18`, `onlyBaseline=1`, `onlyOptimized=1`, `avgBestIou=0.790`, `minBestIou=0.000`, `boxCountDiffFrames=2`, `passed=False`.
- mismatch frame? `onlyBaseline=86`, `onlyOptimized=9`???
- ?먮떒: `蹂대쪟`. 吏湲덇퉴吏??YOLO5Face ?꾨낫 以?frame ?섏? ?띾룄??媛??洹쇱젒?덉?留? ?ㅼ젣 frame set怨?理쒖냼 IoU媛 gate瑜??듦낵?섏? 紐삵븳?? ?ㅽ뙣 異뺤? 紐⑤뜽/threshold curve? YOLO ?꾩슜 track ?꾩쿂由?寃쎄퀎??媛源앸떎. decode??湲곕낯 threshold?먯꽌 ?믪? IoU瑜?蹂댁??쇰?濡??꾩옱 利앷굅留뚯쑝濡?decode ?ㅽ뙣?쇨퀬 蹂댁????딅뒗??

YOLO ?꾩슜 track ?꾩쿂由?1李?蹂댁젙:

- YOLO profile?먯꽌 `MaxLostFillFrames`瑜?`3`?먯꽌 `1`濡?以꾩???
- YOLO profile??`ShortTrackMaxConfidence`瑜?`0.58`?먯꽌 `0.38`濡???톬??
- 紐⑹쟻? YOLO ??좊ː tail box媛 ?붾㈃ 諛?諛⑺뼢?쇰줈 湲멸쾶 extrapolate?섎뒗 false positive瑜?以꾩씠怨? ?ㅼ젣 ?⑤컻 ?쇨뎬 ?꾨낫??frame 86??short-track ?쒓굅?먯꽌 蹂댁〈?섎뒗 寃껋씠??
- FaceONNX profile 媛믪? 蹂寃쏀븯吏 ?딆븯??

蹂댁젙 ??媛숈? 0.12/0.18 ?ш?利?

- FaceONNX baseline: `baselineFrames=19`, `totalMs=54,624ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=19,930ms`.
- A/B 寃곌낵: `common=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.798`, `minBestIou=0.000`, `boxCountDiffFrames=1`, `passed=False`.
- bad frame 濡쒓렇: `boxCountDiff=27`, `lowIou=6,7,8,27`.
- ?먮떒: `蹂대쪟`. frame set mismatch???댁냼?먯?留? YOLO????좊ː edge-tail 諛뺤뒪媛 frame 6~8?먯꽌 FaceONNX baseline蹂대떎 ?꾩そ?쇰줈 ?ш쾶 ?怨? frame 27?????쇨뎬 以??섎굹媛 postprocess?먯꽌 ?뺥빀?섏? ?딅뒗?? ?ㅽ뙣 異뺤? decode蹂대떎??YOLO ?꾩슜 track/post-filter? false-positive/false-negative 寃쎄퀎??媛源앸떎.

YOLO ?꾩슜 track ?꾩쿂由?2李?蹂댁젙:

- `FaceTrackPostProcessOptions`????좊ː tail pruning ?듭뀡??異붽??덈떎.
- YOLO profile?먯꽌 `UnstableTailMaxConfidence=0.40`, `UnstableTailMinStableDetections=3`, `UnstableTailMinIou=0.45`, `UnstableTailMaxAreaChangeRatio=1.8`???ъ슜?쒕떎.
- ?덉젙 track ?ㅼ뿉 遺숈? ??좊ː tail detection??硫댁쟻/IoU 湲곗??쇰줈 湲됯꺽???硫??대떦 tail detection???쒓굅?섍퀬, ?댁쟾 ?덉젙 track 湲곗? lost-fill???곸슜?섍쾶 ?덈떎.
- YOLO profile??`MaxLostFillFrames`???ㅼ떆 `3`?쇰줈 ?먭퀬, `ShortTrackMaxConfidence`??`0.18`濡???떠 ?ㅼ젣 ?⑤컻 ?쇨뎬 ?꾨낫瑜??쒓굅?섏? ?딄쾶 ?덈떎.
- FaceONNX profile? tail pruning 鍮꾪솢??湲곕낯媛?`UnstableTailMaxConfidence=0`)???좎??쒕떎.

2李?蹂댁젙 ??媛숈? 0.12/0.18 ?ш?利?

- FaceONNX baseline: `baselineFrames=19`, `totalMs=55,885ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=16,149ms`.
- A/B 寃곌낵: `common=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`.
- bad frame 濡쒓렇: `boxCountDiff=none`, `lowIou=none`.
- ?먮떒: `3珥?gate ?듦낵`. ???ㅼ젙? ?꾩옱 ???3珥?援ш컙?먯꽌 FaceONNX baseline ?鍮?frame set, 諛뺤뒪 ?? IoU gate瑜??듦낵?덇퀬 ?먮룞 寃異?wall-clock????3.5諛?鍮⑤옄?? ?? ??寃곌낵留뚯쑝濡?YOLO 理쒖쥌 理쒖쟻???꾨즺???꾩껜 ?곸긽 湲곕낯媛??밴꺽?쇰줈 蹂댁????딅뒗??

30珥??뺤옣 smoke:

- ??? `.tmp/srcTest-smoke/smoke-1200-30s.mp4`
- 紐낅졊 議곌굔: `YoloV5Face`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`, `ParallelDetectorCount=2`, `DownscaleRatio=1.0`, `DetectEveryNFrames=1`.
- ?먮룞 寃異? `processed=899`, `detects=899`, `interpolated=0`, `faceMaskFrames=774`, `totalMs=157,604ms`.
- track/ROI: `tracks=144`, `filled=338`, `lostFilled=150`, `removedShort=36`, ROI `attempts=32`, `hits=23`.
- 湲곗〈 FaceONNX 30珥?臾몄꽌 湲곕줉? `processed=899`, `faceMaskFrames=778`, `totalMs=316,366ms`??쇰?濡? frame ?섎뒗 洹쇱젒?섍퀬 ?먮룞 寃異?wall-clock? ??2諛?鍮좊Ⅴ??
- ???ㅽ뻾? baseline A/B 鍮꾧탳媛 ?꾨땲???뺤옣 smoke?대?濡? 30珥?援ш컙??誘명깘/?ㅽ깘/諛뺤뒪 ?먯씠 ?꾩쟾???녿떎怨??⑥젙?섏? ?딅뒗??

30珥?export smoke:

- 媛숈? 30珥?援ш컙?먯꽌 export ?ы븿 ?ㅽ뻾???꾨즺?덈떎.
- ?먮룞 寃異? `processed=899`, `faceMaskFrames=774`, `totalMs=158,063ms`.
- export: `frames=902`, `bitmapMaskFrames=0`, `directFaceFrames=774`, `swsToBgraMs=13,898`, `maskMs=44,094`, `swsToEncMs=22,153`, `encodeMs=4,442`, `totalMs=135,572ms`.
- 湲곗〈 FaceONNX 30珥?medium export 湲곕줉? ?먮룞 寃異?`totalMs=316,366ms`, export `totalMs=127,750ms`, `directFaceFrames=778`?댁뿀?? YOLO???먮룞 寃異쒖씠 ?ш쾶 鍮좊Ⅴ吏留?export??face rect ?섍? 鍮꾩듂??嫄곗쓽 媛숈? 蹂묐ぉ 援ъ“瑜?媛吏꾨떎.
- export output? `.tmp/srcTest-smoke/smoke-1200-30s_blur.mp4`濡??앹꽦?먮떎.

?뱀떆 YOLO5Face 異붿쿇 ?곹깭:

- `YoloV5Face.onnx`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`, YOLO ?꾩슜 unstable-tail pruning ?곸슜 議고빀? ???쒖젏?먮뒗 `議곌굔遺 異붿쿇 ?꾨낫`濡??щ졇??
- 議곌굔遺???댁쑀?????3珥?gate? 30珥?export smoke???듦낵?덉?留? 30珥?援ш컙??baseline A/B ?덉쭏 鍮꾧탳? ?≪븞 ?뺤씤, ?ㅻⅨ ?쒓컙? ???援ш컙, 湲?援ш컙 export 寃利앹? ?꾩쭅 ?⑥븘 ?덇린 ?뚮Ц?대떎.
- ?꾩옱 ?ㅽ뙣 異뺤? ?꾩쟾 ?댁냼媛 ?꾨땲??1李????援ш컙?먯꽌 ?댁냼???곹깭?? ?ㅼ쓬 寃利앹? ?ㅻⅨ ???3珥?援ш컙怨?30珥?baseline A/B ?먮뒗 ?≪븞 寃?좊떎.
- ???먮룞 ?듭뀡???좉퇋 YOLO 湲곕낯 profile??`YOLO5Face`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`濡?留욎톬?? ??λ맂 ?ъ슜???ㅼ젙???덉쑝硫?湲곗〈泥섎읆 ??κ컪???곗꽑 ?곸슜?쒕떎.

?꾩옱 YOLO5Face ?뺣━:

- feature-map decode???숈옉?쒕떎. 湲곕낯 threshold??`avgBestIou=0.973`, `minBestIou=0.953`媛 ?대? ?룸컺移⑦븳??
- 湲곕낯 threshold??鍮좊Ⅴ怨??꾩튂媛 留욎?留?FaceONNX-only frame???щ떎.
- ??? threshold? 以묎컙 threshold??FaceONNX-only frame??以꾩씠?????YOLO-only frame怨?frame mismatch瑜?留뚮뱺??
- 2x2 tile-only??異붿쿇 ?꾨낫媛 ?꾨땲??
- `objectness=0.12`, `confidence=0.18`? 3珥?gate?먯꽌 `faceMaskFrames=19`源뚯? 留욎톬吏留?`onlyBaseline`/`onlyOptimized`? IoU ?ㅽ뙣媛 ?⑥븯??
- YOLO ?꾩슜 track ?꾩쿂由?蹂댁젙 ??frame set mismatch???щ씪議뚯?留? ??좊ː edge-tail 諛뺤뒪 ?먭낵 frame 27 諛뺤뒪 ??李⑥씠媛 ?⑥븘 ?덉쭏 gate瑜??듦낵?섏? 紐삵뻽??
- YOLO ?꾩슜 unstable-tail pruning ?곸슜 ?????3珥?gate???듦낵?덇퀬, 30珥??먮룞 寃異?export smoke???꾨즺?덈떎.
- ?곕씪?????쒖젏??`YoloV5Face.onnx` `0.12/0.18/0.45` ?ㅼ젙? 議곌굔遺 異붿쿇 ?꾨낫濡??먯뿀?? ?꾨옒 9遺?2珥?異붽? gate ?ㅽ뙣???곕씪 理쒖쥌 異붿쿇 ?곹깭???ㅼ떆 蹂댁젙?쒕떎.

FaceONNX ?뚭? ?ш?利?

- YOLO5Face feature-map decode 異붽? ??`dotnet build FaceShield.sln`? 湲곗〈 FFmpeg obsolete warning 7媛쒕쭔 ?④린怨??깃났?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾???ㅼ떆 ?듦낵?덈떎.
- quality gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- ROI-hit ???援ш컙? `attempts=11`, `hits=5`???
- auto-tune short gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=47,742ms`濡??듦낵?덈떎.

YOLO ?꾩슜 ?꾩쿂由?蹂댁젙 ???뚭? ?ш?利?

- `dotnet build FaceShield.sln` ?깃났. 湲곗〈 FFmpeg obsolete warning 7媛쒕쭔 ?⑥븯??
- `git diff --check` ?듦낵.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾 ?깃났.
- quality gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- ROI-hit ???援ш컙? `attempts=11`, `hits=5`???
- auto-tune short gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=75,233ms`濡??듦낵?덈떎. ?꾩옱 ?ㅽ뻾?먯꽌??auto-tune??CPU 2?몄뀡???좏깮?덈떎.

YOLO unstable-tail pruning 異붽? ???뚭? ?ш?利?

- `dotnet build FaceShield.sln` ?깃났. 湲곗〈 FFmpeg obsolete warning 7媛쒕쭔 ?⑥븯??
- `git diff --check` ?듦낵.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾 ?깃났.
- quality gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- ROI-hit ???援ш컙? `attempts=11`, `hits=5`???
- auto-tune short gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=49,033ms`濡??듦낵?덈떎. ?꾩옱 ?ㅽ뻾?먯꽌??auto-tune??CPU 2?몄뀡/default瑜??좏깮?덈떎.

YOLO5Face 湲곕낯 profile ?곌껐 ???뚭? ?ш?利?

- `dotnet build FaceShield.sln` ?깃났. 湲곗〈 FFmpeg obsolete warning 7媛쒕쭔 ?⑥븯??
- `git diff --check` ?듦낵.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾 ?깃났.
- quality gate??`baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=1.000`, `minBestIou=1.000`, `passed=True`???
- ROI-hit ???援ш컙? `attempts=11`, `hits=5`???
- auto-tune short gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `totalMs=47,649ms`濡??듦낵?덈떎. ?꾩옱 ?ㅽ뻾?먯꽌??auto-tune??CPU 2?몄뀡/default瑜??좏깮?덈떎.

?ㅻⅨ ???援ш컙 異붽? gate: 9遺?2珥?援ш컙

- ??? `.tmp/srcTest-smoke/smoke-0900-2s.mp4`
- 紐⑹쟻: 6遺?3珥?gate瑜??듦낵??YOLO5Face `0.12/0.18/0.45` 議고빀???쇨뎬 ?섍? 留롮? ?ㅻⅨ 援ш컙?먯꽌??FaceONNX baseline怨?媛숈? ?덉쭏 gate瑜??듦낵?섎뒗吏 ?뺤씤?쒕떎.

?ㅽ뿕 1: YOLO5Face `objectness=0.12`, `confidence=0.18`, `nms=0.45`

- FaceONNX baseline: `baselineFrames=55`, `totalMs=19,772ms`.
- YOLO5Face optimized: `optimizedFrames=58`, `totalMs=12,314ms`.
- A/B 寃곌낵: `common=55`, `onlyBaseline=0`, `onlyOptimized=3`, `avgBestIou=0.798`, `minBestIou=0.625`, `boxCountDiffFrames=34`, `passed=False`.
- bad frame 濡쒓렇: `boxCountDiff=10,17,18,19,20,21,22,23,24,25,31,32,33,34,35,36,37,38,39,40,...`, `lowIou=11,12,13,14,15,22,24,43,47,49,55,57,60,61`.
- ?먮떒: `蹂대쪟`. 6遺?3珥????gate? ?щ━ ?쇨뎬 ?섍? 留롮? 9遺?援ш컙?먯꽌??YOLO-only frame, 諛뺤뒪 ??李⑥씠, ??? IoU媛 諛쒖깮?쒕떎. YOLO-only ?꾨낫 以??쇰????ㅼ젣 ?쇨뎬?????덉쑝誘濡??ㅼ젣 ?ㅽ깘?쇰줈 ?⑥젙?섏? ?딅뒗?? ?ㅻ쭔 湲곗〈 ?숈옉 蹂????씠 ?ш퀬 overlay/GT 湲곗? ?듦낵 利앷굅媛 遺議깊빐 理쒖쥌 異붿쿇 ?꾨낫濡??좎??섏? ?딅뒗??

?ㅽ뿕 2: YOLO5Face `objectness=0.18`, `confidence=0.25`, `nms=0.45`

- FaceONNX baseline: `baselineFrames=55`, `totalMs=16,920ms`.
- YOLO5Face optimized: `optimizedFrames=54`, `totalMs=10,720ms`.
- A/B 寃곌낵: `common=53`, `onlyBaseline=2`, `onlyOptimized=1`, `avgBestIou=0.793`, `minBestIou=0.625`, `boxCountDiffFrames=29`, `passed=False`.
- bad frame 濡쒓렇: `boxCountDiff=20,21,22,23,24,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,...`, `lowIou=11,12,13,14,15,22,24,43,47,49,55,57,60,61`.
- ?먮떒: threshold瑜??щ━硫?YOLO-only ?꾨낫???쇰? 以꾩?留? FaceONNX-only/YOLO-only frame怨?諛뺤뒪 ?뺥빀 ?ㅽ뙣媛 ?⑥븘 gate瑜??듦낵?섏? 紐삵븳?? ???④퀎留뚯쑝濡??ㅼ젣 誘명깘/?ㅽ깘 ?щ????뺤젙?섏? ?딅뒗??

?ㅽ뿕 3: YOLO5Face `objectness=0.18`, `confidence=0.25`, `nms=0.30`

- FaceONNX baseline: `baselineFrames=55`, `totalMs=17,226ms`.
- YOLO5Face optimized: `optimizedFrames=54`, `totalMs=10,662ms`.
- A/B 寃곌낵: `common=53`, `onlyBaseline=2`, `onlyOptimized=1`, `avgBestIou=0.793`, `minBestIou=0.625`, `boxCountDiffFrames=29`, `passed=False`.
- ?먮떒: NMS瑜???媛뺥븯寃???떠???ㅽ뿕 2? 寃곌낵媛 ?ъ떎??媛숈븯?? ?곕씪????援ш컙??二쇰맂 ?ㅽ뙣 ?먯씤? ?⑥닚 以묐났 諛뺤뒪 NMS媛 ?꾨땲??YOLO5Face???꾨낫 遺꾪룷, FaceONNX ?鍮?諛뺤뒪 ?ш린/?꾩튂 李⑥씠, YOLO ?꾩슜 post-filter/track ?뺥빀 臾몄젣??媛源앸떎.

?꾩옱 YOLO5Face ?먯젙 蹂댁젙:

- `YoloV5Face.onnx` `0.12/0.18/0.45` 議고빀? 6遺?3珥?gate? 12遺?30珥?smoke 湲곗??쇰줈??鍮좊Ⅴ怨??좊쭩?섏?留? 9遺?2珥?異붽? gate?먯꽌 ?ㅽ뙣?덈떎.
- ?곕씪????議고빀??理쒖쥌 `異붿쿇 ?꾨낫`濡?蹂댁? ?딅뒗?? ?꾩옱 ?곹깭??`???6遺?3珥?援ш컙 ?듦낵 ?꾨낫 / 9遺?2珥?諛?6遺?30珥??뺤옣 gate ?ㅽ뙣 / ?꾩껜 異붿쿇 蹂대쪟`??
- Home UI?먯꽌 YOLO瑜??좏깮?덉쓣 ?뚯쓽 珥덇린 profile? 吏湲덇퉴吏 媛???섏? YOLO5Face 議고빀?쇰줈 ?④꺼?먯?留? ??湲곕낯 detector??怨꾩냽 FaceONNX??
- ?ㅼ쓬 YOLO ?묒뾽? 9遺?援ш컙??box count diff? low-IoU frame??湲곗??쇰줈 ?먯씤????遺꾨━?댁빞 ?쒕떎. ?곗꽑?쒖쐞??raw YOLO ?꾨낫 dump, FaceONNX ?鍮??꾨낫 ??李⑥씠, ???쇨뎬/?ㅼ쨷 ?쇨뎬 援ш컙??YOLO ?꾩슜 post-filter, track merge 湲곗? ?ъ“?뺤씠??

9遺?援ш컙 ?먯씤 遺꾨━ 諛?YOLO ?섎떒 ??좊ː track ?꾪꽣:

- `-DumpDetections`? ???frame overlay濡?9遺?援ш컙 mismatch瑜??뺤씤?덈떎.
- frame 20/32 overlay 湲곗? YOLO??異붽? 泥?줉 諛뺤뒪???ㅼそ ?щ엺 ?쇨뎬 ?꾨낫濡?蹂댁씠硫? FaceONNX baseline???볦튇 ?ㅼ젣 ?쇨뎬??媛?μ꽦???덈떎.
- frame 32???몃? 諛뺤뒪????臾쇱껜 ?곸뿭???쇨뎬濡??≪? ?ㅽ깘?대떎.
- ???쇨뎬 諛뺤뒪??YOLO媛 FaceONNX蹂대떎 ???볤쾶 ?〓뒗 寃쏀뼢???덉뼱 `avgBestIou`? `minBestIou`瑜???텣?? ??李⑥씠???⑥닚 NMS 臾몄젣???꾨땲??
- ?곕씪??9遺??ㅽ뙣 異뺤? ?섎굹媛 ?꾨땲???ㅼ쓬???욎뿬 ?덈떎.
  - ?ㅼ젣 ?묒?/?ㅼそ ?쇨뎬 異붽? 寃異? 紐⑤뜽 recall 痢〓㈃?먯꽌??媛쒖꽑?????덉?留? FaceONNX 湲곗? strict frame/box gate?먯꽌??`onlyOptimized`? box count diff濡??≫엺??
  - ??臾쇱껜 ?ㅽ깘: YOLO ?꾩슜 false-positive filter ??곸씠??
  - ???쇨뎬 box shape 李⑥씠: YOLO decode???숈옉?섏?留?FaceONNX ?鍮?諛뺤뒪 ?뺤쓽媛 ?볦뼱 box ?뺥빀 gate瑜???텣??

肄붾뱶 蹂댁젙:

- `FaceTrackPostProcessOptions`??YOLO ?꾩슜 ?섎떒 ??좊ː track ?쒓굅 ?듭뀡??異붽??덈떎.
- `LowerFrameTrackMaxConfidence`, `LowerFrameTrackMinCenterYRatio`, `LowerFrameTrackMinAreaRatio`, `LowerFrameTrackMaxAreaRatio`瑜?異붽??덈떎.
- FaceONNX profile? 湲곕낯媛?`LowerFrameTrackMaxConfidence=0`?쇰줈 鍮꾪솢?깆씠??
- YOLO profile?먯꽌??`LowerFrameTrackMaxConfidence=0.50`, `LowerFrameTrackMinCenterYRatio=0.58`, `LowerFrameTrackMinAreaRatio=0.015`, `LowerFrameTrackMaxAreaRatio=0.045`瑜??곸슜?쒕떎.
- 紐⑹쟻? 9遺?frame 31~38???섏삩 ??臾쇱껜 ?ㅽ깘泥섎읆 ?붾㈃ ?섎떒??以묎컙 ?ш린 ??좊ː track留?醫곴쾶 ?쒓굅?섎뒗 寃껋씠??
- `FaceTrackPostProcessResult`? 濡쒓렇??`removedLower`瑜?異붽???湲곗〈 `removedShort`? 遺꾨━?덈떎.
- `run-srcTest-smoke.ps1`??`-YoloDropShortTrackMaxDetections`, `-YoloShortTrackMaxConfidence`, `-YoloLowerFrameTrackMaxConfidence`瑜?異붽???YOLO track ?꾩쿂由?profile???ㅽ뿕?먯꽌留?議곗젙?????덇쾶 ?덈떎. ??湲곕낯 profile 媛믪? ?좎??쒕떎.

蹂댁젙 ??9遺?2珥?gate:

- FaceONNX baseline: `baselineFrames=55`, `totalMs=20,840ms`.
- YOLO5Face optimized: `optimizedFrames=58`, `totalMs=12,909ms`.
- YOLO track ?꾩쿂由? `removedShort=0`, `removedLower=7`, `rewritten=58`.
- A/B 寃곌낵: `common=55`, `onlyBaseline=0`, `onlyOptimized=3`, `avgBestIou=0.798`, `minBestIou=0.625`, `boxCountDiffFrames=33`, `passed=False`.
- bad frame 濡쒓렇: `boxCountDiff=10,17,18,19,20,21,22,23,24,25,32,33,34,35,36,37,38,39,40,41,...`, `lowIou=11,12,13,14,15,22,24,43,47,49,55,57,60,61`.
- ?먮떒: ?≪븞 ?뺤씤????臾쇱껜 ?ㅽ깘 track ?쇰????쒓굅?먯?留? gate ?ㅽ뙣???遺遺꾩? YOLO-only ?ㅼそ ?쇨뎬 ?꾨낫? ???쇨뎬 諛뺤뒪 ?뺤쓽 李⑥씠?쇱꽌 ???꾪꽣留뚯쑝濡??듦낵?섏? 紐삵븳?? YOLO-only ?ㅼそ ?꾨낫???ㅼ젣 ?쇨뎬 媛?μ꽦???덉쑝誘濡??ㅽ깘?쇰줈 ?⑥젙?섏? ?딅뒗?? ?꾩옱 異붿쿇 ?곹깭??怨꾩냽 `?꾩껜 異붿쿇 蹂대쪟`??

9遺?2珥?YOLO track ?꾩쿂由?sweep:

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `InputSize=640`, `objectness=0.25`, `confidence=0.35`, `nms=0.45`, export skip.
- 寃곌낵 CSV: `.tmp/yolo-sweep/yolo-trackpost-0900-smoke.csv`, `.tmp/yolo-sweep/yolo-trackpost-lowerframe-0900-smoke.csv`

| dropShortMax | shortMaxConf | lowerFrameMaxConf | YOLO totalMs | optimizedFrames | onlyBaseline | onlyOptimized | avgBestIou | minBestIou | avgBaselineCoverage | minBaselineCoverage | boxCountDiffFrames | removedShort | removedLower |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0.18 | 0.50 | 9,001ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 | 0 | 2 |
| 1 | 0.30 | 0.50 | 9,003ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 | 0 | 2 |
| 2 | 0.18 | 0.50 | 8,772ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 | 0 | 2 |
| 2 | 0.30 | 0.50 | 8,855ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 | 0 | 2 |
| 1 | 0.18 | 0.40 | 8,800ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 30 | 0 | 0 |
| 1 | 0.18 | 0.60 | 8,899ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 | 0 | 2 |

- ?먮떒: ??9遺?2珥?議곌굔?먯꽌??吏㏃? track ?쒓굅 媛뺣룄? ?섎떒 track confidence ?곹븳??議곗젙?대룄 strict gate ?ㅽ뙣 ?먯씤??以꾩? ?딅뒗?? `removedLower`瑜?0?쇰줈 ??떠??box diff???ㅽ엳??30?쇰줈 ?섍퀬, 0.60?쇰줈 ?щ젮??湲곕낯媛믨낵 媛숇떎. ?곕씪???꾩옱 ?ㅽ뙣??track ?꾩쿂由??섏튂媛 ?꾨땲??detector ?꾨낫/??諛뺤뒪 ?뺤쓽/?ㅼ젣 ?ㅼそ ?쇨뎬 ?щ? ?먯젙 臾몄젣??媛源앸떎.

異붽? box 蹂댁젙 ?ㅽ뿕:

- `YoloFaceOnnxDetectorOptions`????YOLO5Face 諛뺤뒪瑜?異뺤냼?섎뒗 `LargeBoxWidthScale`, `LargeBoxHeightScale`, `LargeBoxMinAreaRatio` ?듭뀡??異붽??덈떎.
- 湲곕낯媛믪? `1.0/1.0/0.0`?대씪 ??Home YOLO profile?먯꽌??鍮꾪솢?깆씠?? ?꾩옱??`run-srcTest-smoke.ps1`?먯꽌 紐낆떆?곸쑝濡??섍꺼 ?ㅽ뿕?????덈뒗 ?듭뀡?쇰줈留??붾떎.
- 9遺?2珥?gate?먯꽌 `LargeBoxWidthScale=0.84`, `LargeBoxHeightScale=0.97`, `LargeBoxMinAreaRatio=0.03`???곸슜?섎㈃ `avgBestIou=0.786`, `minBestIou=0.631`, `boxCountDiffFrames=33`, `passed=False`濡??ㅽ엳???낇솕?먮떎.
- ?먮떒: ???쇨뎬 諛뺤뒪媛 ?볦? 臾몄젣???뺤씤?먯?留? ?쇨큵 異뺤냼 蹂댁젙? ?ㅻⅨ frame???뺥빀??媛숈씠 源⑥꽌 湲곕낯 profile???ｌ? ?딅뒗??
- `YoloFaceOnnxDetectorOptions`??YOLO5Face landmark span 湲곕컲 ??諛뺤뒪 蹂댁젙 ?ㅽ뿕 ?듭뀡??異붽??덈떎. 愿??smoke ?듭뀡? `-YoloUseLandmarkBoxRefine`, `-YoloLandmarkBoxMinAreaRatio`, `-YoloLandmarkBoxWidthScale`, `-YoloLandmarkBoxHeightScale`, `-YoloLandmarkBoxCenterYOffsetRatio`, `-YoloLandmarkBoxMinOriginalIou`??
- YOLO5Face feature-map??landmark decode??bbox decode? ?ㅻⅤ寃?`raw * anchor + grid * stride` 怨꾩뿴濡?蹂듭썝?댁빞 ?댁꽌 ?대떦 寃쎈줈瑜?遺꾨━?덈떎. 湲곕낯媛믪? 鍮꾪솢?깆씠??Home YOLO profile怨?湲곗〈 FaceONNX 寃쎈줈?먮뒗 ?곹뼢??二쇱? ?딅뒗??
- 9遺?2珥?gate?먯꽌 ?덉쟾?μ튂 湲곕낯媛?`YoloLandmarkBoxMinOriginalIou=0.30`)?쇰줈??landmark 蹂댁젙 eligible ?꾨낫媛 ?덉뿀吏留??곸슜? 0嫄댁씠?덈떎. landmark 湲곕컲 ?꾨낫 諛뺤뒪媛 ?먮옒 YOLO 諛뺤뒪? ?덈Т 硫???덉쟾?μ튂??嫄몃┛ 寃껋쑝濡?蹂몃떎.
- ?덉쟾?μ튂瑜??꾧퀬 媛뺤젣 ?곸슜???ㅽ뿕(`YoloLandmarkBoxWidthScale=1.80`, `YoloLandmarkBoxHeightScale=2.10`, `YoloLandmarkBoxCenterYOffsetRatio=-0.04`, `YoloLandmarkBoxMinOriginalIou=0`)? `baselineFrames=55`, `optimizedFrames=58`, `onlyBaseline=0`, `onlyOptimized=3`, `avgBestIou=0.467`, `minBestIou=0.000`, `boxCountDiffFrames=32`, `passed=False`???
- ?먮떒: landmark span 媛뺤젣 蹂댁젙? ???쇨뎬 諛뺤뒪瑜?怨쇰룄?섍쾶 醫곹엳嫄곕굹 以묒떖??諛??FaceONNX ?뺥빀?????ш쾶 源⑤?濡?異붿쿇 ?꾨낫媛 ?꾨땲?? ??寃곌낵????諛뺤뒪 李⑥씠媛 ?⑥닚 landmark crop 臾몄젣???꾨떂??蹂댁뿬以??

異붽? 吏꾨떒 諛?2?④퀎 ?ㅽ뿕:

- `scripts/run-srcTest-smoke.ps1`??`-DumpCompareDetails`瑜?異붽??덈떎. ???듭뀡??耳쒕㈃ `[SmokeCompareDetail]` 濡쒓렇濡?`onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou` frame??baseline/optimized 諛뺤뒪 `x/y/w/h`, ?뺢퇋??以묒떖?? 硫댁쟻 鍮꾩쑉, confidence瑜?異쒕젰?쒕떎.
- `[SmokeCompareNote]`瑜?異붽???`onlyBaseline`/`onlyOptimized`媛 ?ㅼ젣 ?뺣떟 ?쇰꺼??誘명깘/?ㅽ깘???꾨땲??detector 媛?李⑥씠?꾩쓣 濡쒓렇??紐낆떆?쒕떎.
- `-DumpCompareOverlays`? `-CompareOverlayDir`瑜?異붽??덈떎. ???듭뀡??耳쒕㈃ `onlyBaseline`, `onlyOptimized`, `boxCountDiff`, `lowIou` ???frame??PNG濡???ν븳?? overlay ?됱긽? FaceONNX baseline??鍮④컙 諛뺤뒪, optimized detector媛 泥?줉 諛뺤뒪?? ???대?吏??baseline-diff frame???ㅼ젣 誘명깘/?ㅽ깘?쇰줈 ?먯젙?????ъ슜?섎뒗 ?≪븞 寃???먮즺??
- overlay dump???욎そ ?쇰? frame留???ν븯???쒓퀎瑜?以꾩씠湲??꾪빐 `-CompareOverlayMaxFrames`瑜?異붽??덈떎. 湲곕낯媛믪? reason蹂?`16` frame?대떎.
- `-DumpCompareCrops`, `-CompareCropDir`, `-CompareCropPaddingRatio`瑜?異붽??덈떎. ???듭뀡? `onlyBaseline`, `onlyOptimized`, `boxCountDiff`?먯꽌 baseline怨?IoU `0.35` 誘몃쭔???꾨낫留?crop PNG? `compare-crops.csv`濡???ν븳?? 紐⑹쟻? YOLO-only ?꾨낫媛 ?ㅼ젣 ?쇨뎬?몄? ??臾쇱껜 ?ㅽ깘?몄? ??鍮좊Ⅴ寃??≪븞 遺꾨쪟?섎뒗 寃껋씠??
- crop dump媛 ?욎そ ?쇰? frame留???ν빐 ?꾨컲 `onlyBaseline` ?먯씤 ?뺤씤???섎룞 ffmpeg crop???꾩슂?덈뜕 臾몄젣瑜?以꾩씠湲??꾪빐 `-CompareCropMaxOnlyFrames`, `-CompareCropMaxBoxDiffFrames`瑜?異붽??덈떎. 湲곕낯媛믪? 媛곴컖 `16`?대씪 6遺?30珥?gate??`onlyBaseline=14` 媛숈? 耳?댁뒪????踰덉뿉 紐⑤몢 ??ν븷 ???덈떎.
- `scripts/new-yolo-crop-review.ps1`瑜?異붽??덈떎. `compare-crops.csv`瑜?`crop-review.csv`濡?蹂?섑븯怨? `verdict`??`Face`, `NonFace`, `Unclear`瑜??낅젰????`-Summarize`濡??ㅼ젣 ?먯젙 吏묎퀎瑜??????덇쾶 ?쒕떎. 吏묎퀎 湲곗?? optimized crop??`Face`硫?YOLO recall gain, optimized crop??`NonFace`硫?YOLO false-positive, baseline crop??`Face`硫?YOLO miss, baseline crop??`NonFace`硫?FaceONNX false-positive?? `-QualityGate`瑜?耳쒕㈃ `OptimizedMiss`, `OptimizedFalsePositive`, `Unclear`, `Unreviewed`媛 ?덉슜移??대궡?몄? 寃?ы븯怨??ㅽ뙣 ??exit code `2`瑜?諛섑솚?쒕떎.
- `[SmokeCompare]`??`avgBaselineCoverage`? `minBaselineCoverage`瑜?異붽??덈떎. 湲곗?? FaceONNX baseline 諛뺤뒪 硫댁쟻 以?optimized 諛뺤뒪媛 ??? 鍮꾩쑉?대떎. IoU媛 ??븘??coverage媛 ?믪쑝硫???諛뺤뒪/?뺤쓽 李⑥씠??媛源앷퀬, coverage????쑝硫??ㅼ젣 紐⑥옄?댄겕 誘몄빱踰??꾪뿕?쇰줈 蹂몃떎.
- 9遺?2珥??곸꽭 濡쒓렇 湲곗?, YOLO5Face ?ㅽ뙣????媛吏媛 ?욎뿬 ?덈떎.
  - `onlyOptimized=4,8,9`? frame 10~49??`boxCountDiff`???붾㈃ ?곷떒???묒? ??踰덉㎏ ?쇨뎬 ?꾨낫媛 FaceONNX蹂대떎 癒쇱? ?≫엳???꾩긽?대떎. ?? frame 10??異붽? ?꾨낫??`cx=0.576`, `cy=0.052`, `area=0.00424`, `conf=0.455`???
  - `lowIou`?????쇨뎬 諛뺤뒪???뺤쓽 李⑥씠?? ?? frame 11? FaceONNX `w=535,h=638,area=0.04115` ?鍮?YOLO `w=727.8,h=663.9,area=0.05826`?쇰줈 YOLO ??씠 ???볥떎.
- crop 吏꾨떒 ?ㅽ뻾: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`, `-DumpCompareCrops`, 異쒕젰 `.tmp/yolo-crops/test-0900-yolo5face/`.
- ???ㅽ뻾? crop PNG `15`媛쒖? `compare-crops.csv`瑜??앹꽦?덈떎. `onlyOptimized` frame `4/8/9` 諛?`boxCountDiffOptimizedExtra` frame `17/21/25/33` ???crop? 紐⑤몢 ?붾㈃ ?ㅼそ ?щ엺???놁뼹援대줈 ?≪븞 ?뺤씤?쒕떎.
- ?곕씪????援ш컙??YOLO-only ?묒? ?곷떒 ?꾨낫瑜???臾쇱껜 ?ㅽ깘?쇰줈 ?⑥젙?섎㈃ ???쒕떎. ?꾩옱 利앷굅濡쒕뒗 FaceONNX baseline???볦튇 ?ㅼ젣 ?쇨뎬??YOLO媛 異붽?濡??〓뒗 recall 媛쒖꽑 媛?μ꽦?????щ떎.
- coverage 吏??異붽? ??媛숈? 9遺?2珥?援ш컙???ㅼ떆 ?ㅽ뻾?덈떎. 寃곌낵??FaceONNX baseline `totalMs=13,528ms`, YOLO optimized `totalMs=8,856ms`, `baselineFrames=55`, `optimizedFrames=58`, `onlyBaseline=0`, `onlyOptimized=3`, `avgBestIou=0.798`, `minBestIou=0.625`, `avgBaselineCoverage=0.918`, `minBaselineCoverage=0.714`, `boxCountDiffFrames=33`?댁뿀??
- ?먮떒: ??援ш컙??YOLO???띾룄??FaceONNX ?鍮?鍮좊Ⅴ吏留? ???쇨뎬 coverage 理쒖?媛믪씠 `0.714`???⑥닚??"YOLO 諛뺤뒪媛 ??而ㅼ꽌 ?덉쟾?섎떎"濡??뺣━?????녿떎. ?꾩옱 ?곹깭??異붽? ?쇨뎬 recall 媛?μ꽦怨??쇰? baseline ?쇨뎬 誘몄빱踰??꾪뿕??媛숈씠 ?덈떎.
- 6遺?30珥??뺤옣 gate?먯꽌 媛숈? YOLO5Face profile??export ?ы븿?쇰줈 ?ㅽ뻾?덈떎. FaceONNX baseline? ?먮룞 寃異?`totalMs=339,661ms`, export `totalMs=43,605ms`, `directFaceFrames=83`?댁뿀怨? YOLO optimized???먮룞 寃異?`totalMs=123,243ms`, export `totalMs=43,331ms`, `directFaceFrames=74`???
- 媛숈? 6遺?30珥??뺤옣 gate??A/B 寃곌낵??`baselineFrames=83`, `optimizedFrames=74`, `common=69`, `onlyBaseline=14`, `onlyOptimized=5`, `avgBestIou=0.778`, `minBestIou=0.000`, `avgBaselineCoverage=0.877`, `minBaselineCoverage=0.000`, `boxCountDiffFrames=14`???
- ?먮떒: 30珥??댁긽 援ш컙?먯꽌??YOLO ?먮룞 寃異쒖? FaceONNX ?鍮???`2.76x` 鍮좊Ⅴ吏留? FaceONNX 湲곗? ?꾨씫 frame怨?baseline coverage 0 frame???덉뼱 ?덉쭏 gate ?ㅽ뙣?? export ?쒓컙? ??detector 紐⑤몢 direct face rect 寃쎈줈??嫄곗쓽 媛숆퀬, detector 援먯껜留뚯쑝濡?export 蹂묐ぉ? 以꾩? ?딅뒗??
- 6遺?30珥?crop 吏꾨떒??異붽? ?ㅽ뻾?덈떎. 紐낅졊? 媛숈? YOLO5Face profile??`-SkipExport -DumpCompareDetails -DumpCompareCrops -CompareCropDir .tmp/yolo-crops/test-0600-30s-yolo5face`瑜?遺숈???
- crop 湲곗? `onlyBaseline` frame `204~211`? 諛붾떏??湲덉냽/臾몄뼇 臾쇱껜?怨? 異붽?濡??뺤씤??frame `315/350/709`????臾몄옄/吏곷Ъ ?곸뿭?대씪 ?쇨뎬濡?蹂댁씠吏 ?딆븯?? ?곕씪??6遺?30珥덉쓽 `onlyBaseline=14`瑜?洹몃?濡?YOLO ?ㅼ젣 誘명깘?쇰줈 ?댁꽍?섎㈃ ???쒕떎. ??遺遺꾩? FaceONNX baseline false-positive媛 strict baseline-diff gate瑜??낇솕?쒗궓 ?щ???媛源앸떎.
- 諛섎?濡?`onlyOptimized` frame `167~169/887`??湲덉냽 臾몄뼇?대굹 臾몄옄 ?곸뿭?쇰줈 蹂댁씠??false-positive媛 ?ы븿?섏뼱 ?덈떎. frame `683`? ?묒? ?щ엺 ?쇨뎬 媛?μ꽦???덉쑝??crop留뚯쑝濡??뺤젙?섏? ?딅뒗??
- 6遺?30珥?YOLO false-positive瑜?以꾩씠湲??꾪빐 YOLO profile??吏㏃? ??좊ː track ?쒓굅瑜?`DropShortTrackMaxDetections=3`, `ShortTrackMaxConfidence=0.40`?쇰줈 ?щ젮 ?ㅽ뿕?덈떎. ???ㅼ젙? 6遺?3珥????gate?먯꽌 ?뺤긽 track源뚯? ?쒓굅??`removedShort=5`, `optimizedFrames=10`, `onlyBaseline=9`, `passed=False`瑜?留뚮뱾?덈떎. ?곕씪??湲곕낯 profile?먮뒗 ?ｌ? ?딄퀬 湲곗〈 `DropShortTrackMaxDetections=1`, `ShortTrackMaxConfidence=0.18`濡??섎룎?몃떎.
- ?섎룎由???6遺?3珥?gate瑜??ㅼ떆 ?ㅽ뻾?덇퀬, FaceONNX baseline `totalMs=40,072ms`, YOLO optimized `totalMs=13,595ms`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=0.971`, `minBestIou=0.944`, `avgBaselineCoverage=0.983`, `minBaselineCoverage=0.944`, `boxCountDiffFrames=0`, `passed=True`瑜??뺤씤?덈떎.
- crop/overlay dump 踰붿쐞 ?듭뀡 異붽? ??6遺?3珥?gate瑜??ㅼ떆 ?ㅽ뻾?덇퀬, FaceONNX baseline `totalMs=39,554ms`, YOLO optimized `totalMs=13,138ms`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=0.971`, `minBestIou=0.944`, `avgBaselineCoverage=0.983`, `minBaselineCoverage=0.944`, `boxCountDiffFrames=0`, `passed=True`瑜??뺤씤?덈떎.
- `YoloConfidenceThreshold=0.70` ?ㅽ뿕? ?묒? 異붽? ?꾨낫 ?쇰?瑜?以꾩?吏留????쇨뎬??FaceONNX ?鍮??꾨씫?섏뼱 `onlyBaseline=5,6,7,10`, `avgBestIou=0.777`, `minBestIou=0.000`, `boxCountDiffFrames=13`, `passed=False`??? threshold留??щ━??諛⑹떇? baseline-diff 湲곗??쇰줈 ?꾨씫??留뚮뱺?? ?ㅼ젣 誘명깘 ?щ????대떦 frame overlay ?뺤씤???꾩슂?섎떎.
- `YoloInputSize=800` ?ㅽ뿕? `totalMs=21,556ms`濡?FaceONNX baseline `20,883ms`蹂대떎 ?먮젮議뚭퀬, `avgBestIou=0.783`, `minBestIou=0.553`, `boxCountDiffFrames=37`, `passed=False`??? ?낅젰 ?ш린 ?뺣????띾룄/?덉쭏 紐⑤몢 湲곕낯 ?꾨낫蹂대떎 ?섏걯??
- `run-srcTest-smoke.ps1`??`-YoloUseFaceOnnxRoiRefine` ?ㅽ뿕 ?듭뀡??異붽??덈떎. YOLO 寃곌낵 以?`YoloFaceOnnxRoiMinAreaRatio` ?댁긽 ??諛뺤뒪留?FaceONNX ROI detector濡??ш?異쒗븳??
- 9遺?2珥덉뿉??`-YoloUseFaceOnnxRoiRefine -YoloFaceOnnxRoiMinAreaRatio 0.03 -YoloFaceOnnxRoiMaxCandidates 64`??`candidates=52`, `attempts=50`, `hits=50`, `elapsedMs=22,956`?댁뿀??
- ??2?④퀎 ?ㅽ뿕? `avgBestIou=0.816`?쇰줈 湲곕낯 YOLO `0.798`蹂대떎 議곌툑 ?섏븘議뚯?留?`minBestIou=0.567`, `boxCountDiffFrames=33`, `passed=False`?怨? ROI refine 異붽? ?쒓컙 ?뚮Ц???띾룄 ?댁젏???щ씪吏꾨떎.
- ?먮떒: ?꾩옱 9遺?援ш컙 ?ㅽ뙣??threshold, ?낅젰 ?ш린, ?⑥닚 ??諛뺤뒪 異뺤냼, landmark span 諛뺤뒪 蹂댁젙, ??諛뺤뒪 FaceONNX ROI refiner留뚯쑝濡??닿껐?섏? ?딅뒗?? ?⑥? ?꾨낫?????몃???諛뺤뒪 蹂댁젙 紐⑤뜽, ?묒? ?곷떒 ?쇨뎬 ?꾨낫瑜??ㅼ젣 ?쇨뎬/?ㅽ깘?쇰줈 遺꾨쪟??verifier, ?먮뒗 ?ㅻⅨ YOLO face 紐⑤뜽?대떎.

YOLO threshold sweep harness:

- `scripts/run-yolo-threshold-sweep.ps1`瑜?異붽??덈떎. 湲곗〈 `run-srcTest-smoke.ps1`瑜?諛섎났 ?몄텧??YOLO model/input/objectness/confidence/NMS/tiling 議고빀蹂?寃곌낵瑜?CSV? log濡???ν븳??
- sweep? ?꾨낫 ?섏쭛??以묐떒?섏? ?딄린 ?꾪빐 smoke quality threshold瑜?`MinAvgIou=0`, `MinBestIou=0`, `AllowFrameMismatch=true`濡???떠 ?ㅽ뻾?쒕떎. ?곕씪??CSV??`CollectionGatePassed`???ㅽ뻾 ?섏쭛 ?깃났??媛源뚯슫 媛믪씠硫?理쒖쥌 ?덉쭏 ?듦낵濡?蹂댁? ?딅뒗??
- 理쒖쥌 3珥?gate ?먮떒?⑹쑝濡?`StrictFrameMatchOk`, `StrictIouOk`, `StrictGatePassed` 而щ읆??蹂꾨룄濡?怨꾩궛?쒕떎. 湲곕낯 strict 湲곗?? `onlyBaseline=0`, `onlyOptimized=0`, `boxCountDiffFrames=0`, `avgBestIou>=0.90`, `minBestIou>=0.75`??
- coverage 吏??異붽? ??sweep CSV?먮룄 `AvgBaselineCoverage`, `MinBaselineCoverage` 而щ읆??異붽??덈떎. ??媛믪? ?꾨낫 ?좊퀎 ??IoU? 蹂꾨룄濡??ㅼ젣 baseline face 而ㅻ쾭 ?꾪뿕??蹂대뒗 蹂댁“ 湲곗??대떎.
- 寃利??ㅽ뻾: `.tmp/srcTest-smoke/smoke-0600-3s.mp4`, `YoloV5Face`, `objectness=0.12`, `confidence=0.18`, `nms=0.45` 1耳?댁뒪?먯꽌 `StrictGatePassed=True`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, YOLO `totalMs=13,621ms`瑜??뺤씤?덈떎.
- sweep harness??box shape 蹂댁젙 ?ㅽ뿕 異뺤쓣 異붽??덈떎. `-IncludeLargeBoxScale`? `YoloLargeBoxWidthScale/HeightScale/MinAreaRatio` 諛곗뿴??諛섎났?섍퀬, `-IncludeLandmarkBoxRefine`? landmark 湲곕컲 box refine on/off? landmark scale/offset/min-IoU 諛곗뿴??諛섎났?쒕떎. `-IncludeFaceOnnxRoiRefine`? FaceONNX ROI verifier on/off? min-area/max-candidates 諛곗뿴??諛섎났?쒕떎. `-IncludeTiling -IncludeTileOnly`??non-tile/full+tile/tile-only 紐⑤뱶瑜?媛숈? CSV?먯꽌 鍮꾧탳?쒕떎. `-IncludeTrackPostProcess`??drop-short/lower-frame confidence 異뺤쓣 諛섎났?쒕떎. CSV?먮뒗 tiling mode, track profile, large-box/landmark/ROI 蹂댁젙 ?뚮씪誘명꽣? ROI attempts/hits/elapsedMs媛 ?④퍡 ??λ맂??

9遺?2珥?YOLO5Face objectness sweep:

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `InputSize=640`, `confidence=0.18`, `nms=0.45`, `objectness=0.10/0.12/0.18/0.25`, export skip.
- 寃곌낵 CSV: `.tmp/yolo-sweep/yolo5face-0900-objectness.csv`

| objectness | YOLO totalMs | baselineFrames | optimizedFrames | onlyBaseline | onlyOptimized | avgBestIou | minBestIou | boxCountDiffFrames |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.10 | 11,595ms | 55 | 58 | 0 | 3 | 0.798 | 0.625 | 33 |
| 0.12 | 9,999ms | 55 | 58 | 0 | 3 | 0.798 | 0.625 | 33 |
| 0.18 | 9,504ms | 55 | 58 | 0 | 3 | 0.798 | 0.625 | 33 |
| 0.25 | 9,486ms | 55 | 54 | 2 | 1 | 0.793 | 0.625 | 28 |

- ?먮떒: objectness `0.10~0.18`? ?띾룄留?議곌툑 ?щ씪吏怨?baseline-diff ?덉쭏 吏?쒕뒗 ?ъ떎??媛숇떎. `0.25`??`boxCountDiffFrames`瑜?28源뚯? 以꾩?吏留?FaceONNX-only frame??2媛??앷꺼 strict gate瑜??듦낵?섏? 紐삵븳??

9遺?2珥?YOLO5Face confidence sweep:

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `InputSize=640`, `objectness=0.25`, `nms=0.45`, `confidence=0.18/0.25/0.35/0.50`, export skip.
- 寃곌낵 CSV: `.tmp/yolo-sweep/yolo5face-0900-confidence.csv`

| confidence | YOLO totalMs | baselineFrames | optimizedFrames | onlyBaseline | onlyOptimized | avgBestIou | minBestIou | boxCountDiffFrames | removedLower |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.18 | 8,712ms | 55 | 54 | 2 | 1 | 0.793 | 0.625 | 28 | 6 |
| 0.25 | 8,639ms | 55 | 54 | 2 | 1 | 0.793 | 0.625 | 28 | 4 |
| 0.35 | 8,940ms | 55 | 53 | 2 | 0 | 0.793 | 0.625 | 29 | 2 |
| 0.50 | 8,724ms | 55 | 52 | 3 | 0 | 0.791 | 0.625 | 27 | 0 |

- ?먮떒: confidence瑜??щ━硫?YOLO-only frame怨?`removedLower`??以꾩?留?FaceONNX-only frame???섍굅???좎??섍퀬 `minBestIou=0.625`媛 洹몃?濡??⑤뒗?? 利?confidence ?⑥씪 異뺤? 異붽? ?꾨낫瑜?以꾩씠?????湲곗〈 baseline ?鍮??꾨씫??留뚮뱺??
- ?곕씪??9遺?2珥??ㅽ뙣??objectness/confidence/NMS ?⑥씪 threshold curve濡??닿껐?섏? ?딅뒗?? ?꾩옱 ?ㅽ뙣 ?먯씤? `threshold` ?먯껜蹂대떎 `post-filter/track`怨????쇨뎬 box shape 李⑥씠, 洹몃━怨?YOLO-only ?묒? ?곷떒 ?꾨낫???ㅼ젣 ?쇨뎬 ?щ? ?먯젙 遺?ъ뿉 媛源앸떎. ?ㅼ쓬 理쒖쟻???곗꽑?쒖쐞???꾨낫瑜??ㅼ젣 ?쇨뎬/鍮꾩뼹援대줈 遺꾨쪟?섎뒗 verifier, ???쇨뎬 box shape 蹂댁젙 紐⑤뜽/?꾨왂, ?먮뒗 ?ㅻⅨ YOLO face 紐⑤뜽 鍮꾧탳??

9遺?2珥?YOLO5Face box refine smoke:

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `InputSize=640`, `objectness=0.25`, `confidence=0.35`, `nms=0.45`, export skip.
- 寃곌낵 CSV: `.tmp/yolo-sweep/yolo-box-refine-0900-smoke.csv`, `.tmp/yolo-sweep/yolo-largebox-scale-0900-smoke.csv`

| 蹂댁젙 | YOLO totalMs | optimizedFrames | onlyBaseline | onlyOptimized | avgBestIou | minBestIou | avgBaselineCoverage | minBaselineCoverage | boxCountDiffFrames |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| off | 9,100ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 |
| landmark default | 8,935ms | 52 | 3 | 0 | 0.467 | 0.205 | 0.498 | 0.218 | 29 |
| large scale 0.85/0.85 | 9,023ms | 53 | 2 | 0 | 0.718 | 0.556 | 0.759 | 0.557 | 29 |
| large scale 0.85/0.90 | 8,834ms | 53 | 2 | 0 | 0.751 | 0.589 | 0.796 | 0.590 | 29 |
| large scale 0.90/0.85 | 8,793ms | 53 | 2 | 0 | 0.723 | 0.564 | 0.779 | 0.574 | 29 |
| large scale 0.90/0.90 | 9,259ms | 53 | 2 | 0 | 0.756 | 0.596 | 0.817 | 0.608 | 29 |

- ?먮떒: landmark span ?щ컯?깃낵 ?⑥닚 large-box 異뺤냼??紐⑤몢 湲곗? off蹂대떎 ?섏걯?? ?뱁엳 landmark default??baseline coverage瑜??ш쾶 源⑤?濡?異붿쿇 profile???ｌ? ?딅뒗??
- ?곕씪??9遺?2珥덉쓽 ???쇨뎬 box 李⑥씠???꾩옱 援ы쁽???⑥닚 異뺤냼/landmark 蹂댁젙?쇰줈 ?닿껐?섏? ?딅뒗?? ?ㅼ쓬 ?꾨낫???ㅼ젣 ?쇨뎬/鍮꾩뼹援?verifier ?먮뒗 ?ㅻⅨ YOLO face 紐⑤뜽 鍮꾧탳濡??섍릿??

9遺?2珥?YOLO5Face FaceONNX ROI verifier sweep:

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `InputSize=640`, `objectness=0.25`, `confidence=0.35`, `nms=0.45`, export skip, `FaceOnnxRoiMinAreaRatio=0.03`, `FaceOnnxRoiMaxCandidates=32`.
- 寃곌낵 CSV: `.tmp/yolo-sweep/yolo-faceonnx-roi-0900-smoke.csv`

| FaceONNX ROI refine | YOLO totalMs | optimizedFrames | onlyBaseline | onlyOptimized | avgBestIou | minBestIou | avgBaselineCoverage | minBaselineCoverage | boxCountDiffFrames | ROI attempts | ROI hits | ROI elapsed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| off | 8,947ms | 53 | 2 | 0 | 0.793 | 0.625 | 0.916 | 0.714 | 29 | - | - | - |
| on | 9,566ms | 53 | 2 | 0 | 0.795 | 0.567 | 0.931 | 0.714 | 29 | 32 | 32 | 9,684ms |

- ?먮떒: FaceONNX ROI verifier????議곌굔?먯꽌 紐⑤뱺 ROI ?꾨낫瑜?hit濡?遊ㅼ?留? frame ?섏? box count diff瑜?以꾩씠吏 紐삵뻽怨?`minBestIou`??????븘議뚮떎. 異붽? ROI 鍮꾩슜??諛쒖깮?쒕떎. ?곕씪???꾩옱 ?⑥닚 FaceONNX ROI verifier??9遺?援ш컙 異붿쿇 profile???ｌ? ?딅뒗??

6遺?3珥?YOLO5Face tiling mode sweep:

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0600-3s.mp4`, `YoloV5Face`, `InputSize=640`, `objectness=0.12`, `confidence=0.18`, `nms=0.45`, export skip, `2x2 overlap=0.15`.
- 寃곌낵 CSV: `.tmp/yolo-sweep/yolo5face-0600-tiling-modes.csv`

| tiling | tileOnly | YOLO totalMs | optimizedFrames | onlyBaseline | onlyOptimized | avgBestIou | minBestIou | avgBaselineCoverage | minBaselineCoverage | boxCountDiffFrames | strict |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| false | false | 12,710ms | 19 | 0 | 0 | 0.971 | 0.944 | 0.983 | 0.944 | 0 | pass |
| true | false | 58,955ms | 46 | 0 | 27 | 0.849 | 0.047 | 0.874 | 0.047 | 3 | fail |
| true | true | 48,611ms | 41 | 3 | 25 | 0.536 | 0.000 | 0.602 | 0.000 | 2 | fail |

- ?먮떒: YOLO5Face??6遺?3珥????gate?먯꽌??selective tiling???꾩????섏? ?딅뒗?? full+tile怨?tile-only 紐⑤몢 ?꾨낫 frame??怨쇳븯寃??섎━嫄곕굹 baseline face coverage瑜?源④퀬, ?띾룄??non-tile蹂대떎 ?⑥뵮 ?먮━?? ?꾩옱 異붿쿇 profile? tiling off瑜??좎??쒕떎.

9遺?2珥?overlay 寃??

- 紐낅졊 議곌굔: `.tmp/srcTest-smoke/smoke-0900-2s.mp4`, `YoloV5Face`, `objectness=0.25`, `confidence=0.35`, `nms=0.45`, `-DumpCompareOverlays`.
- 異쒕젰 ?꾩튂: `.tmp/yolo-overlays/test-0900-yolo5face/`
- `onlyBaseline-frame-000006.png`? `onlyBaseline-frame-000007.png`??鍮④컙 諛뺤뒪???붾㈃ ?ㅼそ ?щ엺 ?쇨뎬濡??≪븞 ?뺤씤?쒕떎. ?곕씪????threshold?먯꽌???⑥닚 baseline-diff媛 ?꾨땲???ㅼ젣 ?쇨뎬 ?꾨씫?쇰줈 蹂????덈떎.
- `boxCountDiff-frame-000021.png`??泥?줉 諛뺤뒪媛 ?ㅼそ ?쇨뎬???↔퀬 ???꾨㈃ ?쇨뎬?????볤쾶 ?〓뒗?? ??frame? ?ㅼ젣 ?쇨뎬 異붽? 寃異?媛?μ꽦怨????쇨뎬 box shape 李⑥씠媛 媛숈씠 ?욎씤 ?щ???
- ??overlay 寃곌낵??9遺?援ш컙 ?ㅽ뙣媛 ?⑥닚 threshold 臾몄젣媛 ?꾨땲?? ?ㅼ젣 ?ㅼそ ?쇨뎬 蹂댁〈怨????쇨뎬 諛뺤뒪 ?뺥빀???숈떆??留뚯”?댁빞 ?섎뒗 臾몄젣?꾩쓣 蹂댁뿬以??

蹂댁젙 ??6遺?3珥??뚭? gate:

- FaceONNX baseline: `baselineFrames=19`, `totalMs=57,937ms`.
- YOLO5Face optimized: `optimizedFrames=19`, `totalMs=17,651ms`.
- YOLO track ?꾩쿂由? `removedShort=0`, `removedLower=0`, `rewritten=19`.
- A/B 寃곌낵: `common=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`.
- ?먮떒: ?섎떒 ??좊ː track ?꾪꽣媛 湲곗〈 6遺?3珥??듦낵 援ш컙??源⑥????딆븯??
- script 吏꾨떒 ?듭뀡 異붽? ?꾩뿉??6遺?3珥?gate???ㅼ떆 ?듦낵?덈떎. 理쒖떊 ?ㅽ뻾? FaceONNX baseline `totalMs=120,601ms`, YOLO optimized `totalMs=20,367ms`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`??? ???ㅽ뻾??FaceONNX baseline ?쒓컙? 媛숈? ?몄뀡??遺???곹뼢??而ㅼ꽌 ?띾룄 鍮꾧탳 湲곗?媛믪쑝濡?怨좎젙?섏? ?딅뒗??
- coverage 吏??異붽? ??湲곕낯 鍮꾪솢???곹깭??6遺?3珥?gate瑜??ㅼ떆 ?ㅽ뻾?덈떎. 理쒖떊 ?ㅽ뻾? FaceONNX baseline `totalMs=39,714ms`, YOLO optimized `totalMs=13,028ms`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=0.971`, `minBestIou=0.944`, `avgBaselineCoverage=0.983`, `minBaselineCoverage=0.944`, `boxCountDiffFrames=0`, `passed=True`???
- 6遺?30珥??뺤옣 gate??媛숈? profile?먯꽌 ?ㅽ뙣?덈떎. FaceONNX baseline ?먮룞 寃異?`totalMs=339,661ms`, YOLO ?먮룞 寃異?`totalMs=123,243ms`, export??媛곴컖 `43,605ms`? `43,331ms`?吏留? A/B媛 `onlyBaseline=14`, `onlyOptimized=5`, `avgBestIou=0.778`, `minBestIou=0.000`, `avgBaselineCoverage=0.877`, `minBaselineCoverage=0.000`, `boxCountDiffFrames=14`??異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??
- 6遺?30珥?crop 吏꾨떒 寃곌낵, `onlyBaseline` ???frame ?곷떦?섎뒗 ?쇨뎬???꾨땶 湲덉냽 臾몄뼇/臾몄옄/吏곷Ъ ?곸뿭?쇰줈 蹂댁??? ???뺤옣 gate ?ㅽ뙣??YOLO 誘명깘留뚯씠 ?꾨땲??FaceONNX baseline false-positive? YOLO false-positive媛 ?④퍡 ?욎씤 baseline-diff ?ㅽ뙣濡?遺꾨쪟?쒕떎.
- 吏㏃? ??좊ː YOLO track ?쒓굅瑜?媛뺥솕?섎뒗 ?ㅽ뿕? 6遺?3珥?gate瑜?源⑥꽌 ?먭린?덈떎. ?꾩옱 YOLO 湲곕낯 profile? 6遺?3珥?gate ?듦낵媛믪쑝濡??섎룎由??곹깭??
- crop/overlay dump 踰붿쐞 ?듭뀡 異붽? ?꾩뿉??6遺?3珥?YOLO gate???ㅼ떆 ?듦낵?덈떎. 理쒖떊 ?ㅽ뻾? FaceONNX baseline `totalMs=39,554ms`, YOLO optimized `totalMs=13,138ms`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=0`, `passed=True`???
- Home ?먮룞 ?ㅼ젙 ??μ쓣 `SettingsVersion=5`濡??щ━怨?YOLOv8-Face/YOLO5Face蹂?紐⑤뜽 寃쎈줈, threshold, input, tiling profile??蹂꾨룄 ???蹂듭썝?섎룄濡??섏젙?덈떎. 湲곗〈 ?⑥씪 `Yolo*` ?꾨뱶??active profile ?명솚?⑹쑝濡??좎??섍퀬, 湲곗〈 version 4 ?ㅼ젙? ?좏깮??YOLO 紐⑤뜽 profile濡?留덉씠洹몃젅?댁뀡?쒕떎.
- profile ???遺꾨━ ??6遺?3珥?YOLO gate瑜??ㅼ떆 ?ㅽ뻾?덈떎. FaceONNX baseline `totalMs=40,080ms`, YOLO optimized `totalMs=13,653ms`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=0.971`, `minBestIou=0.944`, `avgBaselineCoverage=0.983`, `minBaselineCoverage=0.944`, `boxCountDiffFrames=0`, `passed=True`瑜??뺤씤?덈떎.
- crop review template ?앹꽦 寃利앹쓣 ?섑뻾?덈떎. 9遺?2珥?YOLO5Face crop? `.tmp/yolo-crops/test-0900-yolo5face/crop-review.csv`??15嫄댁쑝濡??앹꽦?먭퀬, 6遺?30珥?YOLO5Face crop? `.tmp/yolo-crops/test-0600-30s-yolo5face/crop-review.csv`??26嫄댁쑝濡??앹꽦?먮떎. `verdict`媛 梨꾩썙吏湲??꾩뿉???ㅼ젣 ?ㅽ깘/誘명깘 count濡??곗? ?딅뒗??
- 9遺?2珥?crop review 寃곌낵: `Reviewed=15`, `OptimizedActualFace=15`, `OptimizedFalsePositive=0`, `OptimizedMiss=0`, `BaselineFalsePositive=0`, `Unclear=0`. ??援ш컙??YOLO-only/YOLO-extra ?묒? ?곷떒 ?꾨낫??紐⑤몢 ?щ엺 ?놁뼹援대줈 蹂댁뿬 YOLO false-positive媛 ?꾨땲??YOLO recall gain?쇰줈 遺꾨쪟?쒕떎.
- 6遺?30珥?crop review 寃곌낵: `Reviewed=26`, `OptimizedActualFace=1`, `OptimizedFalsePositive=10`, `OptimizedMiss=0`, `BaselineFalsePositive=14`, `Unclear=1`. ??援ш컙??strict baseline-diff ?ㅽ뙣??YOLO 誘명깘蹂대떎 FaceONNX false-positive? YOLO false-positive媛 媛숈씠 ?욎씤 臾몄젣濡?遺꾨쪟?쒕떎. ?? ??review?????crop ?≪븞 ?먯젙?대ŉ ?꾩껜 ?곸긽 GT ?쇰꺼 寃利앹? ?꾨땲??
- crop review quality gate 寃곌낵: 9遺?2珥?crop review??`passed=True`?怨? 6遺?30珥?crop review??`passed=False`, `optimizedFalsePositive=10`, `unclear=1`, `exitCode=2`??? ?곕씪??YOLO5Face ?꾩옱 profile? 9遺?2珥덉쓽 recall 媛쒖꽑 媛?μ꽦?먮룄 遺덇뎄?섍퀬 6遺?30珥??ㅼ젣 crop review 湲곗? false-positive ?뚮Ц??理쒖쥌 異붿쿇 ?꾨낫濡??щ━吏 ?딅뒗??
- unreviewed gate 寃利? 9遺?2珥?review CSV??`verdict`瑜??꾩떆濡?紐⑤몢 鍮꾩슫 ?뚯씪??`-QualityGate`瑜??ㅽ뻾?덉쓣 ??`passed=False`, `unreviewed=15`, `exitCode=2`瑜??뺤씤?덈떎. ?곕씪???ㅼ젣 ?먯젙??鍮꾩뼱 ?덈뒗 crop review??異붿쿇 gate濡??듦낵?섏? ?딅뒗??
- `scripts/verify-yolo-crop-review.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???꾩옱 YOLO5Face review CSV 湲곗??쇰줈 9遺?2珥?review媛 ?듦낵?섎뒗吏? 6遺?30珥?review媛 false-positive ?뚮Ц???ㅽ뙣?섎뒗吏瑜???踰덉뿉 寃利앺븳?? 理쒖떊 ?ㅽ뻾? `yolo5face-0900-review-pass exitCode=0`, `yolo5face-0600-30s-review-fail exitCode=2`, `all requested checks passed`???
- `scripts/verify-auto-mosaic-default.ps1 -RunYoloCropReview` ?듭뀡??異붽??덈떎. ???듭뀡? 湲곗〈 FaceONNX default verifier瑜?洹몃?濡??ㅽ뻾????`verify-yolo-crop-review.ps1`瑜??몄텧??YOLO crop review gate???④퍡 ?뺤씤?쒕떎.
- `scripts/verify-yolo-profile-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???뚯뒪 invariant濡?FaceONNX/YOLO backend ?좏깮, YOLOv8-Face/YOLO5Face ?좏깮吏, `SettingsVersion=6`, 紐⑤뜽蹂?`YoloV8*`/`Yolo5*` ????꾨뱶, active legacy profile ?명솚 ?꾨뱶, YOLO filter profile 遺꾨━, FaceONNX threshold? YOLO threshold 遺꾨━, `YoloFaceOnnxDetectorOptions`??threshold/tiling ?듭뀡 議댁옱瑜??뺤씤?쒕떎.
- `scripts/verify-auto-mosaic-default.ps1 -RunYoloProfileState` ?듭뀡??異붽??덈떎. ???듭뀡? 湲곗〈 FaceONNX default verifier瑜?洹몃?濡??ㅽ뻾????`verify-yolo-profile-state.ps1`瑜??몄텧??backend/profile 遺꾨━ invariant???④퍡 ?뺤씤?쒕떎.
- `scripts/find-yolo-review-filter-candidates.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??review ?쇰꺼??梨꾩썙吏?optimized crop留???곸쑝濡?confidence/area/cx/cy/aspect 湲곕컲 ?⑥닚 drop rule ?꾨낫瑜??곗텧?쒕떎. ?꾩옱 9遺?2珥?+ 6遺?30珥?review ?쒕낯 湲곗??쇰줈??`confidence <= 0.5 and cy >= 0.08`??review row?먯꽌 `DroppedFace=0`, `DroppedNonFace=9`濡?媛??媛뺥븯寃?蹂댁?吏留? ??媛믪? review crop ?쒕낯??????꾨낫??肉??꾩껜 frame gate ?듦낵瑜??섎??섏? ?딅뒗??
- ???꾨낫瑜?YOLO ?꾩슜 ?ㅽ뿕 ?듭뀡?쇰줈 寃利앺븯湲??꾪빐 `YoloFaceOnnxDetectorOptions.UseLowConfidencePositionFilter`, `LowConfidencePositionMaxConfidence`, `LowConfidencePositionMinCenterYRatio`瑜?異붽??덈떎. 湲곕낯媛믪? 鍮꾪솢?깆씠誘濡?Home YOLO profile怨?湲곗〈 FaceONNX 寃쎈줈?먮뒗 ?곹뼢??二쇱? ?딅뒗?? `run-srcTest-smoke.ps1`?먮뒗 `-YoloUseLowConfidencePositionFilter`, `-YoloLowConfidencePositionMaxConfidence`, `-YoloLowConfidencePositionMinCenterYRatio` ?ㅽ뿕 ?몄옄瑜?異붽??덈떎.
- low-confidence position filter ?ㅽ뿕 寃곌낵: 6遺?3珥????gate?먯꽌 `maxConfidence=0.50`, `minCenterY=0.08`? YOLO optimized媛 `faceMaskFrames=9`, `onlyBaseline=10`, `passed=False`媛 ?섏뼱 ?먭린?쒕떎. ???쏀븳 `maxConfidence=0.35`, `minCenterY=0.08`??`faceMaskFrames=11`, `onlyBaseline=8`, `avgBestIou=0.890`, `minBestIou=0.000`, `passed=False`??? ?곕씪??review crop 湲곗??쇰줈 醫뗭븘 蹂댁씤 ?⑥닚 ?꾩튂/?좊ː???꾪꽣???ㅼ젣 ???frame gate瑜?源⑤ŉ 異붿쿇 profile???ｌ? ?딅뒗??
- `scripts/run-yolo-review-filter-sweep.ps1`瑜?異붽??덈떎. low-confidence position filter ?꾨낫瑜?`run-srcTest-smoke.ps1`濡?諛섎났 ?ㅽ뻾??CSV/log濡??④린???꾩슜 sweep harness?? 6遺?3珥????gate ?ш?利?寃곌낵??`.tmp/yolo-sweep/yolo-review-filter-0600-smoke.csv`??湲곕줉?덈떎. ?꾪꽣 鍮꾪솢?깆? `baselineFrames=19`, `optimizedFrames=19`, `onlyBaseline=0`, `onlyOptimized=0`, `avgBestIou=0.971`, `minBestIou=0.944`, `StrictGatePassed=True`?怨? `maxConfidence=0.35/minCenterY=0.08`? `optimizedFrames=11`, `onlyBaseline=8`, `avgBestIou=0.890`, `minBestIou=0.000`, `StrictGatePassed=False`, `maxConfidence=0.50/minCenterY=0.08`? `optimizedFrames=9`, `onlyBaseline=10`, `StrictGatePassed=False`???
- area 湲곕컲 ?꾨낫 寃利앹쓣 ?꾪빐 `YoloFaceOnnxDetectorOptions.UseSmallAreaFilter`, `SmallAreaMaxAreaRatio`瑜?異붽??섍퀬 `run-srcTest-smoke.ps1`/`run-yolo-review-filter-sweep.ps1`???ㅽ뿕 ?몄옄瑜??곌껐?덈떎. 湲곕낯媛믪? 鍮꾪솢?깆씠?? review ?쒕낯?먯꽌??`area <= 0.0035`媛 `DroppedFace=0`, `DroppedNonFace=5`?吏留? 6遺?3珥????gate sweep `.tmp/yolo-sweep/yolo-review-filter-0600-area-smoke.csv`?먯꽌??`maxArea=0.0030`怨?`0.0035` 紐⑤몢 `optimizedFrames=5`, `onlyBaseline=14`, `StrictGatePassed=False`??? ?곕씪???⑥닚 ?묒? 諛뺤뒪 ?쒓굅?????援ш컙???ㅼ젣 ?꾩슂??YOLO ?꾨낫瑜?媛숈씠 ?쒓굅?섎?濡??먭린?쒕떎.
- YOLOv8-Face ?泥?紐⑤뜽濡?`lindevs/yolov8-face`??`yolov8s-face-lindevs.onnx`瑜?`.tmp/models/`?먮쭔 ?대젮諛쏆븘 ?뚯뒪?명뻽?? `sha256sum`? `0a6d19f2f68d7f0cc8104ab5c9eaa54b63e298f91dcfefd4be897f94a1561d02`?怨? `inspect-onnx-outputs.ps1` 湲곗? input? `images=1x3x640x640`, output? `output0=1x5x8400`?대씪 湲곗〈 YOLOv8 decode 寃쎈줈? 媛숈븯?? 6遺?3珥????gate sweep `.tmp/yolo-sweep/yolov8s-0600-smoke.csv`?먯꽌 `objectness/confidence` 議고빀 `0.05/0.05`, `0.05/0.20`, `0.20/0.05`, `0.20/0.20`瑜??뺤씤?덉?留?紐⑤몢 `optimizedFrames=6`, `onlyBaseline=13`, `avgBestIou=0.749`, `minBestIou=0.712`, `StrictGatePassed=False`??? ?곕씪??YOLOv8s???꾩옱 pipeline 湲곗? 異붿쿇 ?꾨낫濡??щ━吏 ?딅뒗??
- YOLOv8-Face ?泥?紐⑤뜽濡?`lindevs/yolov8-face`??`yolov8l-face-lindevs.onnx`??`.tmp/models/`?먮쭔 ?대젮諛쏆븘 ?뚯뒪?명뻽?? `sha256sum`? `52dc39e46a7316398c95d30dd669a641382c9fdd8b675ad32aa65585bf820ea0`?怨? input/output shape? `images=1x3x640x640`, `output0=1x5x8400`?대씪 湲곗〈 YOLOv8 decode 寃쎈줈? 媛숇떎. 6遺?3珥????gate sweep `.tmp/yolo-sweep/yolov8l-0600-smoke.csv`?먯꽌 `objectness/confidence=0.05/0.05`??`optimizedFrames=24`, `onlyBaseline=1`, `onlyOptimized=6`, `avgBestIou=0.354`, `minBestIou=0.000`, `StrictGatePassed=False`, ?섎㉧吏 `0.05/0.20`, `0.20/0.05`, `0.20/0.20`? 紐⑤몢 `optimizedFrames=11`, `onlyBaseline=10`, `onlyOptimized=2`, `avgBestIou=0.741`, `minBestIou=0.695`, `StrictGatePassed=False`??? 媛?耳?댁뒪??YOLO `totalMs`????`64珥???YOLOv8l? ?덉쭏怨??띾룄 ?묒そ?먯꽌 異붿쿇 ?꾨낫濡??щ━吏 ?딅뒗??

?꾩옱 留덇컧 ?곹깭:

- ?꾨즺: YOLO backend ?좏깮, YOLOv8-Face/YOLO5Face model profile 遺꾨━ ??? FaceONNX auto-tune 寃쎈줈? YOLO 寃쎈줈 遺꾨━, FaceONNX/SCRFD/YOLO filter profile 遺꾨━, YOLO sweep/overlay/crop/coverage 吏꾨떒 ?꾧뎄 異붽?, threshold/tiling/track ?꾩쿂由?box 蹂댁젙/FaceONNX ROI verifier ?ㅽ뿕 湲곕줉.
- ?좎?: ??湲곕낯 detector??FaceONNX?? Home?먯꽌 YOLO瑜??좏깮?덉쓣 ?뚯쓽 珥덇린 profile? ?꾩옱源뚯? 媛???섏? YOLO5Face `objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`, tiling off 議고빀???좎??쒕떎.
- 蹂대쪟: YOLO5Face??6遺?3珥????gate?먯꽌??FaceONNX ?鍮???3諛?鍮좊Ⅴ怨?strict gate瑜??듦낵?덉?留? 9遺?2珥덉? 6遺?30珥??뺤옣 gate?먯꽌??frame/box ?뺥빀???듦낵?섏? 紐삵뻽?? ?곕씪??FaceONNX ?泥?湲곕낯媛??먮뒗 理쒖쥌 異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??
- 湲곗?: ?꾩옱 A/B gate??`onlyBaseline`/`onlyOptimized`???ㅼ젣 ?뺣떟 ?쇰꺼???꾨땲??detector 媛?李⑥씠?? crop/overlay濡??쇰? ?뺤씤??寃곌낵 FaceONNX false-positive, YOLO false-positive, YOLO 異붽? recall 媛?μ꽦??紐⑤몢 ?욎뿬 ?덉뿀?? 洹몃옒????寃곌낵留뚯쑝濡??쒖そ 紐⑤뜽??紐⑤뱺 ?ㅽ깘/誘명깘???뺤젙?섏? ?딅뒗??
- ?⑥? ?먮떒: YOLO瑜??ㅼ젣 諛고룷 ?꾨낫濡?蹂대젮硫?label 湲곕컲 face/non-face 寃利? Avalonia GUI?먯꽌 ?닿린/誘몃━蹂닿린/?몄쭛/export ?섎룞 smoke, 10遺꾧툒 ?꾩껜 援ш컙 ?덉쭏/?띾룄 痢≪젙??蹂꾨룄濡??뺤씤?댁빞 ?쒕떎. 紐⑤뜽 license/諛고룷 ?먮떒? 2026-05-23 ?ы솗??湲곗? repo 紐⑤뜽 異붿쟻 湲덉?, installer ?꾩닔 踰덈뱾 湲덉?, ?ъ슜??吏???몃? 紐⑤뜽 寃쎈줈 ?먮뒗 ?붾（??濡쒖뺄 `Models/Yolo` 寃쎈줈 ?좎?濡??뺣━?쒕떎.

YOLO ?ㅽ뙣 ?먯씤 遺꾨쪟:

<!-- yolo-conclusion-state: no-final-yolo-recommendation; default=FaceONNX; ab-gate-not-ground-truth; required=label-gui-10min; distribution=no-bundled-yolo-model; axes=model,decode,preprocess,post-filter,track,roi,tiling,small-face,box-refine,speed -->

| ?꾨낫/?꾨왂 | ?꾩옱 ?먯젙 | 媛源뚯슫 ?ㅽ뙣 異?| 洹쇨굅 | ?ㅼ쓬 ?먮떒 |
| --- | --- | --- | --- | --- |
| YOLOv8n 640 | 異붿쿇 ?꾨낫 ?놁쓬 | 紐⑤뜽/threshold curve, post-filter | 6遺?3珥?low-threshold??`onlyBaseline=11`, `avgBestIou=0.603`, `minBestIou=0.048`; 9遺?2珥덈룄 threshold瑜???텛硫?YOLO-only ?꾨낫媛 留롪퀬 ?щ━硫?FaceONNX-only frame???앷릿?? decode??`output0=1x5x8400` 寃쎈줈濡??ㅽ뻾?먯쑝誘濡?decode 遺덈뒫?쇰줈 蹂댁????딅뒗?? | YOLOv8n? ??pipeline?먯꽌 蹂대쪟?쒕떎. |
| YOLOv8m 640 | 異붿쿇 ?꾨낫 ?놁쓬 | 紐⑤뜽/threshold curve, post-filter | low-threshold??`onlyBaseline=3`, `onlyOptimized=13`, `avgBestIou=0.406`; middle-threshold??`onlyBaseline=9`, `avgBestIou=0.674`??3珥?gate瑜??듦낵?섏? 紐삵뻽?? | 30珥?export ?뺤옣 ??곸씠 ?꾨땲?? |
| YOLOv8s 640 | 異붿쿇 ?꾨낫 ?놁쓬 | 紐⑤뜽 ?꾨낫 | 6遺?3珥?sweep??紐⑤뱺 `objectness/confidence` 議고빀?먯꽌 `optimizedFrames=6`, `onlyBaseline=13`, `StrictGatePassed=False`??? 湲곗〈 YOLOv8 decode shape怨?媛숈쑝誘濡??꾩옱 利앷굅??decode蹂대떎 紐⑤뜽 ?꾨낫 遺?곹빀??媛源앸떎. | 異붿쿇 ?꾨낫?먯꽌 ?쒖쇅?쒕떎. |
| YOLOv8l 640 | 異붿쿇 ?꾨낫 ?놁쓬 | 紐⑤뜽 ?꾨낫, ?띾룄 | 6遺?3珥?sweep?먯꽌 `0.05/0.05`??`onlyOptimized=6`, `avgBestIou=0.354`, ?섎㉧吏 議고빀? `onlyBaseline=10`?닿퀬, 媛?耳?댁뒪 `totalMs`媛 ??`64珥???? | ?덉쭏怨??띾룄 ?묒そ?먯꽌 ?쒖쇅?쒕떎. |
| YOLO5Face 0.12/0.18/0.45 | ?꾩껜 異붿쿇 蹂대쪟 | 紐⑤뜽/box definition, post-filter/track, label 遺??| 6遺?3珥?strict gate???듦낵?섍퀬 鍮좊Ⅴ吏留? 9遺?2珥덈뒗 ???쇨뎬 box shape 李⑥씠? YOLO-only ?ㅼそ ?쇨뎬 ?꾨낫媛 ?욎?怨?6遺?30珥?crop review??YOLO false-positive? FaceONNX false-positive媛 ?④퍡 ?덉뿀?? | ??湲곕낯媛믪쑝濡??밴꺽?섏? ?딅뒗?? YOLO ?좏깮 ??珥덇린 profile濡쒕쭔 ?좎??쒕떎. |
| selective tiling | 異붿쿇 ?꾨낫 ?놁쓬 | tiling ?꾨왂 | YOLO5Face 6遺?3珥?full+tile/tile-only??`onlyOptimized` 利앷?, coverage ??? ???띾룄 鍮꾩슜??留뚮뱾?덈떎. YOLOv8n tile-only??`onlyBaseline=10`, `onlyOptimized=7`?댁뿀?? | ?꾩옱 ?꾨왂? tiling off ?좎?. |
| low-confidence position filter | 異붿쿇 ?꾨낫 ?놁쓬 | post-filter | review crop ?쒕낯?먯꽌???좊쭩?덉?留?6遺?3珥?frame gate?먯꽌 `onlyBaseline=8~10`, `StrictGatePassed=False`媛 ?먮떎. | frame gate瑜?源⑤?濡??먭린?쒕떎. |
| small-area filter | 異붿쿇 ?꾨낫 ?놁쓬 | small-face 湲곗?/post-filter | review ?쒕낯?먯꽌??`NonFace` ?쇰?瑜??쒓굅?덉?留?6遺?3珥?gate?먯꽌 ?꾩슂???묒? ?쇨뎬 ?꾨낫源뚯? ?쒓굅??`optimizedFrames=5`, `onlyBaseline=14`媛 ?먮떎. | ?⑥닚 ?묒? 諛뺤뒪 ?쒓굅???먭린?쒕떎. |
| FaceONNX ROI verifier | 異붿쿇 ?꾨낫 ?놁쓬 | ROI refine ?꾨왂 | 9遺?2珥덉뿉??ROI hit??留롮븯吏留?`boxCountDiffFrames`瑜?以꾩씠吏 紐삵뻽怨?`minBestIou`媛 ??븘議뚯쑝硫?ROI 鍮꾩슜??異붽??먮떎. | ?꾩옱 ?⑥닚 ROI verifier??異붿쿇 profile???ｌ? ?딅뒗?? |
| large-box/landmark box refine | 異붿쿇 ?꾨낫 ?놁쓬 | box refine ?꾨왂 | ?⑥닚 異뺤냼? landmark span ?щ컯??紐⑤몢 9遺?2珥덉뿉??`avgBestIou`/coverage瑜??낇솕?쒖섟?? | ?ㅻⅨ box 蹂댁젙 紐⑤뜽/?꾨왂???꾩슂?섎떎. |

??遺꾨쪟 湲곗??먯꽌 `?꾩쿂由????꾩옱 二쇱슂 ?ㅽ뙣 異뺤쑝濡??뺤젙?섏? ?딅뒗?? 媛숈? 紐⑤뜽 shape?먯꽌 ?낅젰 ?ш린 ?뺣?(`YoloInputSize=800`)???띾룄/?덉쭏??紐⑤몢 ?섎튌議뚭퀬, YOLOv8 怨꾩뿴? metadata媛 `1x3x640x640` 怨좎젙?대씪 ?꾩쿂由щ쭔?쇰줈 ?닿껐?먮떎??利앷굅媛 ?녿떎. `decode`??YOLOv8 generic output怨?YOLO5Face feature-map output??媛곴컖 ?ㅽ뻾?섍퀬 ?쇰? ?믪? IoU 寃곌낵媛 ?덉뼱 ?꾩옱 二쇱슂 ?ㅽ뙣 異뺤쑝濡?蹂댁? ?딅뒗??

YOLO 紐⑤뜽 異쒖쿂/license/諛고룷 ?먮떒:

<!-- yolo-license-source-state: checked=2026-05-23; yolov8-face=lindevs-mit-with-yolov8-initial-weights-caveat; yolo5face=huggingface-gpl-3.0; ultralytics-yolov8=agpl-3.0-or-enterprise; bundle=blocked; source-gate=pass -->

| 紐⑤뜽 ?꾨낫 | 異쒖쿂 | ?쒖떆 license/諛고룷 硫붾え | ?꾩옱 ?쒗뭹 諛고룷 ?먮떒 |
| --- | --- | --- | --- |
| `yolov8n/s/m/l-face-lindevs.onnx` | `lindevs/yolov8-face` GitHub release: https://github.com/lindevs/yolov8-face | 2026-05-23 ?뺤씤 湲곗? ??μ냼??MIT license濡??쒖떆?쒕떎. README??pretrained model??WIDERFace濡??숈뒿?먭퀬 YOLOv8 models瑜?initial weights濡??ъ슜?덈떎怨??ㅻ챸?쒕떎. Ultralytics 怨듭떇 臾몄꽌??YOLOv8 models媛 AGPL-3.0 ?먮뒗 Enterprise license ??곸씠?쇨퀬 ?ㅻ챸?섎?濡? ??μ냼 license? 蹂꾧컻濡?upstream YOLOv8 weight/license ?곹뼢???덈떎. | ?깅뒫 gate ?ㅽ뙣 諛?upstream license caveat ?뚮Ц??repo/installer???ы븿?섏? ?딅뒗?? 濡쒖뺄 `.tmp/models/` ?ㅽ뿕 ?꾨낫濡쒕쭔 ?붾떎. |
| `YoloV5Face.onnx` | Hugging Face `hayashiLin/deepfacelivemodels`: https://huggingface.co/hayashiLin/deepfacelivemodels/blob/main/YoloV5Face.onnx | 2026-05-23 ?뺤씤 湲곗? Hugging Face ?뚯씪 ?섏씠吏??license??`gpl-3.0`?쇰줈 ?쒖떆?쒕떎. 濡쒖뺄 ?ㅽ뿕 SHA-256? ?댁쟾 ?뺤씤媛믨낵 ?쇱튂?덈떎. | ?깅뒫 理쒖쥌 異붿쿇 蹂대쪟 諛?GPL-3.0 license 由ъ뒪???뚮Ц??repo/installer???ы븿?섏? ?딅뒗?? ?ъ슜?먭? 吏곸젒 寃쎈줈瑜?吏?뺥븯???ㅽ뿕???꾨낫濡쒕쭔 ?붾떎. |

諛고룷 ?곹깭 invariant:

- YOLO 紐⑤뜽 ?뚯씪? repo??異붿쟻?섏? ?딅뒗??
- Home????湲곕낯 detector??怨꾩냽 `FaceONNX`??
- YOLO detector 肄붾뱶??蹂꾨룄 YOLO `.csproj`媛 ?꾨땲??`FaceShield.csproj` ?덉쓽 SDK-style compile ???`Services/FaceDetection/YoloFaceOnnxDetector.cs`)?쇰줈 ?좎??쒕떎.
- YOLO???ъ슜?먭? 吏곸젒 ?좏깮?섍퀬 ?ъ슜??吏???몃? 紐⑤뜽 寃쎈줈瑜?吏?뺥븯嫄곕굹, ?붾（??濡쒖뺄 `Models/Yolo` ?대뜑??湲곕낯 ?뚯씪紐낆쑝濡???`.onnx`瑜??먮룞 ?먯깋?섎뒗 backend/profile 寃쎈줈濡쒕쭔 ?좎??쒕떎.
- 紐⑤뜽 ?뚯씪 ?⑸웾怨?license 由ъ뒪???뚮Ц??repo??紐⑤뜽???ｌ? ?딅뒗?? ???Home??detector ?좏깮 以꾩뿉??`YOLO ?ㅼ슫濡쒕뱶` 踰꾪듉???붾떎. 踰꾪듉? FaceONNX 湲곕낯 ?곹깭?먯꽌???꾩튂媛 蹂댁씠吏留?湲곗〈 `CanDownloadYoloModel` 議곌굔 ?뚮Ц??YOLO backend ?좏깮 ?쒖뿉留??쒖꽦?붾릺硫? ?좏깮??YOLO 醫낅쪟??紐⑤뜽???ъ슜??濡쒖뺄 ???곗씠???대뜑(`FaceShield/Models/Yolo`)濡??대젮諛쏄퀬 `AutoYoloModelPath`???먮룞 諛섏쁺?쒕떎.
- ?ㅼ슫濡쒕뱶 踰꾪듉???꾩옱 留ㅽ븨? YOLO5Face??Hugging Face `hayashiLin/deepfacelivemodels`??`YoloV5Face.onnx`, YOLOv8-Face??GitHub `lindevs/yolov8-face` release `1.0.1`??`yolov8n-face-lindevs.onnx`?? 2026-05-24 ?뺤씤 湲곗? Hugging Face ?뚯씪 ?섏씠吏??`YoloV5Face.onnx`瑜?28.3MB, license `gpl-3.0`?쇰줈 ?쒖떆?덇퀬, GitHub release??`1.0.1` latest release? ONNX opset 19 re-save changelog瑜??쒖떆?덈떎.
- `FaceShield.csproj`??濡쒖뺄 `Models/Yolo/*.onnx`媛 ?덉쓣 ??異쒕젰/?쇰툝由ъ떆??蹂듭궗?섏?留? `.gitignore`媛 ?대떦 紐⑤뜽 ?뚯씪???쒖쇅?쒕떎. 利??붾（???덉뿉 ???섎뒗 ?덉뼱??repo/installer ?꾩닔 踰덈뱾濡?異붿쟻?섏? ?딅뒗??
- 理쒖떊 異쒖쿂 湲곗??쇰줈??YOLOv8 upstream license ?곹뼢怨?YoloV5Face GPL-3.0 由ъ뒪?ш? ?⑥븘 ?덉쑝誘濡? ?꾩옱 ?쒗뭹 諛고룷 ?뺤콉? `YOLO 紐⑤뜽 踰덈뱾 湲덉?`, `FaceONNX 湲곕낯媛??좎?`, `?ъ슜??吏???몃? 紐⑤뜽 寃쎈줈, ???곗씠???ㅼ슫濡쒕뱶 寃쎈줈, ?먮뒗 ?붾（??濡쒖뺄 Models/Yolo 寃쎈줈留??덉슜`?쇰줈 怨좎젙?쒕떎. ???곹깭?먯꽌 FaceShield repo??YOLO 紐⑤뜽 ?뚯씪??異붿쟻?섏? ?딄퀬, installer/CI publish??濡쒖뺄 紐⑤뜽 ?뚯씪??紐낆떆?곸쑝濡?議댁옱???뚮쭔 蹂듭궗 洹쒖튃???곸슜?쒕떎.
- `scripts/verify-yolo-distribution-state.ps1`?????곹깭 以?異붿쟻 媛?ν븳 遺遺꾩쓣 寃?ы븳??

?뚭? 寃利?

- `dotnet build FaceShield.sln` ?깃났. WSL `dotnet build` 吏곸젒 ?ㅽ뻾 湲곗? warning/error??0媛쒖??? Windows verifier ?대? build?먯꽌??湲곗〈 FFmpeg obsolete warning 7媛쒓? ?ㅼ떆 異쒕젰?먮떎.
- `git diff --check` ?듦낵.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1` 湲곕낯 ?ㅽ뻾 ?깃났.
- default verifier??track postprocess policy, 6遺?3珥?FaceONNX all-frame parallel quality gate, ROI-hit ???援ш컙, short auto-tune provider gate瑜?紐⑤몢 ?듦낵?덈떎.
- 理쒖떊 short auto-tune gate??`FaceOnnxDetector/CPU`, `pipe-parallel`, `processed=150`, `detects=150`, `interpolated=0`, `faceMaskFrames=19`, `totalMs=38,164ms`???
- crop review workflow 異붽? ??PowerShell parser 寃利앹쓣 ?듦낵?덈떎. `new-yolo-crop-review.ps1`濡?9遺?2珥?6遺?30珥?crop review template ?앹꽦怨?`-Summarize` ?ㅽ뻾???뺤씤?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-crop-review.ps1` ?ㅽ뻾 ?깃났. 9遺?2珥?review pass? 6遺?30珥?review fail??紐⑤몢 湲곕???寃곌낵濡??뺤씤?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1 -RunYoloCropReview` ?ㅽ뻾 ?깃났. 湲곕낯 FaceONNX verifier? YOLO crop review wrapper媛 紐⑤몢 ?듦낵?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-profile-state.ps1` ?ㅽ뻾 ?깃났.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1 -RunYoloProfileState` ?ㅽ뻾 ?깃났. 湲곕낯 FaceONNX verifier? YOLO profile-state invariant媛 紐⑤몢 ?듦낵?덈떎.
- `scripts/verify-yolo-conclusion-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???꾩옱 臾몄꽌媛 `理쒖쥌 YOLO 異붿쿇 ?꾨낫 ?놁쓬`, ??湲곕낯 detector `FaceONNX`, A/B gate媛 ?뺣떟 ?쇰꺼???꾨땲?쇰뒗 caveat, ?꾨낫蹂??ㅽ뙣 ?먯씤 遺꾨쪟, ?⑥? label/GUI/10遺꾧툒 寃利???ぉ??怨꾩냽 ?ы븿?섎뒗吏 ?뺤씤?쒕떎.
- `scripts/verify-yolo-distribution-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??YOLO 紐⑤뜽 ?뚯씪??repo??異붿쟻?섏? ?딄퀬, 臾몄꽌??`no-bundled-yolo-model`, `GPL-3.0`, `MIT`, `upstream YOLOv8 weight/license` 諛고룷 caveat媛 ?좎??섎뒗吏 ?뺤씤?쒕떎. ?댄썑 ?붾（??濡쒖뺄 `Models/Yolo` ?먮룞 ?먯깋 ?뺤콉怨?`.gitignore` 紐⑤뜽 ?쒖쇅, `FaceShield.csproj` 濡쒖뺄 紐⑤뜽 蹂듭궗 洹쒖튃???④퍡 寃?ы븯?꾨줉 ?뺤옣?덈떎.
- 2026-05-23 湲곗? `lindevs/yolov8-face`, Hugging Face `YoloV5Face.onnx`, Ultralytics license 臾몄꽌瑜??ㅼ떆 ?뺤씤?덈떎. 寃곕줎? 蹂寃??놁씠 YOLO 紐⑤뜽 ?뚯씪??repo/installer/CI publish ?꾩닔 ?뚯씪濡?踰덈뱾?섏? ?딄퀬, YOLO backend???ъ슜?먭? 吏곸젒 紐⑤뜽 寃쎈줈瑜?吏?뺥븯???ㅽ뿕 寃쎈줈濡쒕쭔 ?좎??쒕떎.
- `scripts/verify-yolo-representative-gate.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??`.tmp/models/YoloV5Face.onnx`? 6遺?3珥????clip???덉쓣 ???꾩옱 YOLO5Face 珥덇린 profile(`objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`, tiling off)??YOLO lost-fill 6?꾨젅???곸슜 ??`baselineFrames=19`, `optimizedFrames=20`, `onlyBaseline=0`, `onlyOptimized=1`, `avgBestIou=0.971`, `minBestIou=0.944`, `SmokeQualityGate passed=True`瑜??좎??섎뒗吏 ?뺤씤?쒕떎.
- `scripts/verify-yolo-extended-gate.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??媛숈? YOLO5Face 珥덇린 profile??6遺?30珥?30珥?clip?먯꽌 YOLO lost-fill 6?꾨젅???곸슜 ?꾩뿉??`SmokeQualityGate passed=False`, `baselineFrames=83`, `optimizedFrames=81`, `onlyBaseline=13`, `onlyOptimized=11`, `avgBestIou=0.770`, `minBestIou=0.000`, `boxCountDiffFrames=15`, YOLO `lostFilled=24`濡??ㅽ뙣?섎뒗吏 ?뺤씤?쒕떎. ??gate??3珥?????듦낵瑜?理쒖쥌 異붿쿇?쇰줈 ?ㅽ빐?섏? ?딄쾶 留뚮뱶???뺤옣 ?ㅽ뙣 ?뚭? 寃利앹씠??
- `scripts/verify-yolo-extended-export-gate.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??媛숈? 6遺?30珥?clip?먯꽌 export源뚯? ?ㅼ젣 ?섑뻾????`[ExportRunSummary]`媛 FaceONNX `directFaceFrames=83`, YOLO `directFaceFrames=81`??湲곕줉?섍퀬, ?댄썑 A/B ?덉쭏 gate媛 ?ㅽ뙣?섎뒗吏 ?뺤씤?쒕떎.
- `scripts/verify-yolo-track-hold-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???⑹꽦 `FrameMaskProvider`?먯꽌 YOLO track profile怨?媛숈? `MaxLostFillFrames=6`, `MaxConfirmedTrackHoldFrames=8`???곸슜?? 媛숈? track ?대???湲?no-face gap frame 13~19媛 蹂닿컙/hold?섍퀬 track 醫낅즺 ??frame 21~26? lost-fill濡??좎??섎ŉ frame 27?먯꽌??硫덉텛?붿? 寃利앺븳?? ?숈떆??1?꾨젅?꾩쭨由??쏀븳 ?꾨낫???뺤젙 track?쇰줈 ?좎??섏? ?딄퀬 ?쒓굅?섎뒗吏 ?ㅼ젣 `FaceTrackInterpolator` ?ㅽ뻾?쇰줈 寃利앺븳??
- `scripts/verify-yolo-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??`verify-yolo-profile-state.ps1`, `verify-yolo-track-hold-state.ps1`, `verify-yolo-crop-review.ps1`, full-GT harness, `verify-yolo-conclusion-state.ps1`, `verify-yolo-distribution-state.ps1`, `verify-yolo-goal-audit-state.ps1`, `verify-yolo-top-level-require-complete-state.ps1`, `verify-yolo-manual-readiness-state.ps1`, `verify-yolo-completion-audit-state.ps1`瑜?臾띠뼱 YOLO ?꾩슜 ?곹깭瑜?鍮좊Ⅴ寃??ы솗?명븳?? `-RunRepresentativeGate`瑜?遺숈씠硫?`verify-yolo-representative-gate.ps1`源뚯? ?ㅽ뻾?섍퀬, `-RunExtendedGate`瑜?遺숈씠硫?`verify-yolo-extended-gate.ps1`源뚯? ?ㅽ뻾?쒕떎. `-RunExtendedExportGate`瑜?遺숈씠硫?`verify-yolo-extended-export-gate.ps1`源뚯? ?ㅽ뻾?쒕떎. `verify-auto-mosaic-default.ps1 -RunYoloState`?먯꽌??媛숈? wrapper瑜??몄텧?????덇퀬, `-RunYoloRepresentativeGate`/`-RunYoloExtendedGate`/`-RunYoloExtendedExportGate`瑜??④퍡 遺숈씠硫??대떦 YOLO gate源뚯? ?ы븿?쒕떎. `-RequireYoloComplete`瑜?遺숈씠硫??곸쐞 湲곕낯 寃利앹뿉?쒕룄 `yolo-require-complete-guard`瑜?癒쇱? ?ㅽ뻾?섍퀬 `-RunYoloState`瑜??붿떆??YOLO completion audit??strict ?꾨즺 議곌굔???붽뎄?쒕떎. `verify-yolo-top-level-require-complete-state.ps1`???꾩옱 pending marker?먯꽌 ???곸쐞 strict 寃쎈줈媛 `goal marked complete missing text: complete=true`濡?鍮좊Ⅴ寃??ㅽ뙣?섍퀬 FaceONNX quality gate源뚯? ?대젮媛吏 ?딅뒗吏 ?뚭? 寃利앺븳?? ?대븣 `verify-yolo-state.ps1 -RequireComplete`??湲?YOLO state ?꾩껜瑜??뚭린 ?꾩뿉 `completion-audit-require-complete-guard`瑜?癒쇱? ?ㅽ뻾???꾩옱 pending 利앷굅?먯꽌??鍮좊Ⅴ寃??ㅽ뙣?쒕떎. `-RunTenMinuteState`??10遺?runner/preflight ?곹깭瑜??뺤씤?섍퀬, `-RequireTenMinuteClip`???④퍡 遺숈씠硫?以鍮꾨맂 10遺?clip源뚯? 寃?ы븳??
- `scripts/verify-yolo-gt-label-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??crop review CSV???쒕낯 verdict瑜?GT??face/non-face count濡?蹂?섑빐, A/B diff瑜??ㅼ젣 ?ㅽ깘/誘명깘?쇰줈 ?⑥젙?섏? ?딅뒗 湲곗????뚭? 寃利앺븳??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-gt-label-state.ps1` ?ㅽ뻾 ?깃났. 9遺?2珥?pass ?쒕낯? `rows=15`, `reviewed=15`, `yoloTruePositive=15`, `yoloFalsePositive=0`, `yoloMiss=0`, `faceOnnxFalsePositive=0`?댁뿀?? 6遺?30珥?fail ?쒕낯? `rows=26`, `reviewed=26`, `unclear=1`, `yoloTruePositive=1`, `yoloFalsePositive=10`, `yoloMiss=0`, `faceOnnxFalsePositive=14`???
- `scripts/verify-yolo-full-gt-label-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???꾩껜 frame/track GT CSV? detector prediction CSV ?먮뒗 `-DumpDetections` 濡쒓렇??`[SmokeDetection]` ?쇱씤??IoU 湲곗??쇰줈 鍮꾧탳??`truePositive`, `miss`, `falsePositive`, `lowIou`瑜?怨꾩궛?쒕떎. ?꾩옱??full GT ?곗씠?곌? ?꾩쭅 ?놁쑝誘濡?`-SelfTest`濡?matcher? quality gate ?숈옉留?寃利앺븳??
- `scripts/new-yolo-full-gt-template.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??detector prediction CSV ?먮뒗 `-DumpDetections` 濡쒓렇??`[SmokeDetection]` ?쇱씤?먯꽌 full GT ?쇰꺼留곸슜 CSV ?쒗뵆由우쓣 留뚮뱺?? ?쒗뵆由우쓽 `label=face` ?됰쭔 GT濡??됯??섍퀬, blank/nonface ?됱? detector false-positive ?꾨낫濡??④릿?? detector媛 ?볦튇 ?ㅼ젣 ?쇨뎬? ?щ엺??CSV???됱쓣 異붽??댁빞 ?쒕떎.
- `scripts/verify-yolo-full-gt-template-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???ㅼ젣 YOLO smoke??`[SmokeDetection]` 濡쒓렇?먯꽌 `new-yolo-full-gt-template.ps1`濡??쇰꺼留??쒗뵆由우쓣 ?앹꽦?섍퀬, ?꾩닔 而щ읆/source/blank label ?곹깭瑜??뺤씤?쒕떎.
- `scripts/new-yolo-full-gt-review-package.ps1`? `scripts/verify-yolo-full-gt-review-package-state.ps1`瑜?異붽??덈떎. ?쇰꺼留??쒗뵆由?CSV? ?곸긽?먯꽌 detection crop PNG? `full-gt-review.csv`瑜??앹꽦???щ엺??`label=face/nonface`? evidence note瑜?梨꾩슱 ???덇쾶 ?쒕떎. `-IncludeFullFrameReview`瑜?遺숈씠硫?`full-frame-review.csv`, ?먮낯 frame image, detection box媛 洹몃젮吏?overlay frame image, frame蹂?`candidateSummary`???④퍡 ?앹꽦??detection crop???≫엳吏 ?딆? visible face瑜?蹂꾨룄濡??ㅼ틪?섍퀬, 鍮좎쭊 ?쇨뎬? `full-gt-review.csv`??manual missed-face row濡?異붽??섍쾶 ?쒕떎. ?앹꽦 ?⑦궎吏?먮뒗 crop/frame/overlay/?꾨낫 ?붿빟, ?낅젰 洹쒖튃, CSV row key, pending ?꾨뱶, ?낅젰 ?⑦꽩??釉뚮씪?곗??먯꽌 ?묒쓣 ???덈뒗 `review-index.html`???ы븿?쒕떎. 湲곗〈 ?щ엺???묒꽦 以묒씤 CSV瑜???? ?딄퀬 HTML ?덈궡留?媛깆떊?????덈룄濡?`-RefreshIndexOnly`??異붽??덈떎.
- `scripts/verify-yolo-full-gt-reviewed-state.ps1`瑜?異붽??덈떎. ?щ엺??梨꾩슫 `full-gt-review.csv`??label/reviewStatus/evidenceNotes ?곹깭瑜?寃?ы븳 ??`verify-yolo-full-gt-label-state.ps1`濡?IoU 湲곕컲 GT quality gate瑜??ㅽ뻾?쒕떎. ?ㅼ젣 full GT ?먯젙?먮뒗 `-RequireFullFrameReview -RequireArtifacts`瑜?遺숈뿬 `full-frame-review.csv`??`missedFaceCount`, `missedFaceRowsAdded`, evidence, frame蹂?manual missed-face row ?섏? review crop/frame/overlay artifact ?뚯씪??紐⑤몢 ?쇱튂?섎뒗吏??寃?ы빐???쒕떎. `-AllowQualityGateFailure`瑜?遺숈씠硫??쇰꺼/?꾪떚?⑺듃 寃?섎뒗 ?듦낵?쒗궎??`passed=False`? `failureAllowed=True`瑜?異쒕젰??YOLO ?꾨낫 ?ㅽ뙣瑜?異붿쿇 ?꾨낫 ?놁쓬 寃쎈줈??利앷굅濡??④릿?? ?꾩옱 ?ㅼ젣 由щ럭 CSV???꾩쭅 blank label?대?濡?湲곕낯 self-test留?wrapper???ы븿?쒕떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-full-gt-label-state.ps1 -SelfTest` ?ㅽ뻾 ?깃났. self-test 湲곗? `gtFaces=2`, `predictions=2`, `truePositive=1`, `miss=1`, `falsePositive=1`??湲곕?媛믪쑝濡??뺤씤?덈떎.
- synthetic prediction CSV?먯꽌 `new-yolo-full-gt-template.ps1`濡?template???앹꽦?섍퀬, ???됱쓣 `label=face`濡??뺤젙????`verify-yolo-full-gt-label-state.ps1 -GtCsv ... -PredictionCsv ... -MaxMisses 0 -MaxFalsePositives 1 -MaxLowIou 0` ?ㅽ뻾???깃났?덈떎. data mode 湲곗? `gtFaces=1`, `predictions=2`, `truePositive=1`, `miss=0`, `falsePositive=1`?댁뿀??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-full-gt-template-state.ps1` ?ㅽ뻾 ?깃났. ?ㅼ젣 YOLO `-DumpDetections` smoke 濡쒓렇 `.tmp/yolo-ten-minute-detection-smoke/yolo-ten-minute-yolo-only-20260523-022022.log`?먯꽌 `.tmp/yolo-full-gt/yolo-detection-smoke-template.csv`瑜??앹꽦?덇퀬 20媛?row? ?쇰꺼留곸슜 ?꾩닔 而щ읆???뺤씤?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-full-gt-review-package-state.ps1` ?ㅽ뻾 ?깃났. `.tmp/yolo-full-gt/yolo-detection-smoke-template.csv`??20媛?row ?꾩껜?먯꽌 crop PNG? `.tmp/yolo-full-gt/review-package-smoke/full-gt-review.csv`瑜??앹꽦?섍퀬, detection ?꾨낫媛 ?섏삩 unique frame 19媛쒖쓽 full-frame review row, frame image, detection overlay frame image, frame蹂?candidate summary, `review-index.html`???앹꽦???꾩닔 由щ럭 而щ읆怨??대?吏 ?뚯씪???뺤씤?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-full-gt-reviewed-state.ps1 -SelfTest` ?ㅽ뻾 ?깃났. synthetic reviewed CSV?먯꽌 face/nonface ?쇰꺼, evidence note, crop/frame/overlay artifact ?뚯씪??寃?ы븯怨? full-frame review self-test?먯꽌 `missedFaceCount=0`/evidence瑜??뺤씤???? full GT evaluator媛 `gtFaces=1`, `predictions=2`, `truePositive=1`, `miss=0`, `falsePositive=1` 議곌굔?쇰줈 ?듦낵?섎뒗吏 ?뺤씤?덈떎. 異붽? manual-missed self-test?먯꽌??frame 30??`missedFaceCount=1`, `missedFaceRowsAdded=1`, manual missed-face row 1媛쒓? frame蹂꾨줈 ?쇱튂?섍퀬, evaluator媛 `gtFaces=2`, `truePositive=1`, `miss=1`, `falsePositive=1` 議곌굔?쇰줈 ?듦낵?섎뒗吏 ?뺤씤?덈떎. negative self-test 5媛쒕룄 異붽???`missedFaceCount`/`missedFaceRowsAdded` 遺덉씪移? full-frame missed ?좎뼵 ?鍮?manual row ?꾨씫, evidence note ?꾨씫, crop artifact ?꾨씫, frame artifact ?꾨씫??紐⑤몢 ?ㅽ뙣濡??≫엳?붿? ?뺤씤?쒕떎.
- AI-assisted visual review candidate濡?`.tmp/yolo-full-gt/review-package-smoke/full-gt-review-reviewed-candidate.csv`? `.tmp/yolo-full-gt/review-package-smoke/full-frame-review-reviewed-candidate.csv`瑜??앹꽦?덈떎. ???꾨낫 ?쇰꺼? ?щ엺???뺤젙??GT媛 ?꾨땲誘濡?理쒖쥌 異붿쿇 洹쇨굅濡??ъ슜?섏? ?딅뒗?? ?뺣? ?뺤씤?먯꽌 frame 7??detection crop? ?쇨뎬???꾨땲?????닿묠 履쎌뿉 嫄몃졇怨? 媛숈? frame??諛곌꼍 ?쇨뎬? detection crop????씠吏 ?딆? 寃껋쑝濡?蹂댁젙?덈떎. ?꾨낫 湲곗??쇰줈 `verify-yolo-full-gt-reviewed-state.ps1 -RequireFullFrameReview -RequireEvidence -RequireArtifacts -MaxMisses 1 -MaxFalsePositives 13 -MaxLowIou 1`??`gtFaces=8`, `truePositive=7`, `miss=1`, `falsePositive=13`, `lowIou=1`?쇰줈 ?듦낵?쒕떎. 媛숈? ?꾨낫瑜?strict gate `-MaxFalsePositives 0`?쇰줈 ?ㅽ뻾?섎㈃ `passed=False`, exit code `2`?? ?꾩옱 YOLO5Face smoke 寃곌낵??AI ?꾨낫 ?쇰꺼 湲곗??쇰줈??誘명깘 1媛쒖? ?ㅽ깘 13媛??뚮Ц??異붿쿇 ?꾨낫媛 ?꾨땲?? `scripts/verify-yolo-full-gt-reviewed-candidate-state.ps1`媛 ??tolerant pass? strict fail??紐⑤몢 ?ш?利앺븯硫? `scripts/verify-auto-mosaic-default.ps1 -RunYoloFullGtReviewedCandidateState`濡??⑤룆 ?몄텧?????덈떎.
- `scripts/new-yolo-human-review-draft.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??pending `full-gt-review.csv`/`full-frame-review.csv`? AI-assisted candidate CSV瑜?鍮꾧탳??`.tmp/yolo-manual-gates/human-review-draft/full-gt-review-human-draft.csv`, `full-frame-review-human-draft.csv`, `human-review-draft-report.md`瑜??앹꽦?쒕떎. 珥덉븞? `candidateLabel`, `candidateMissedFaceCount`, `candidateEvidenceNotes` 媛숈? ?꾨낫 而щ읆留?梨꾩슦怨??ㅼ젣 `label`, `reviewStatus`, `evidenceNotes`, `missedFaceCount` ??理쒖쥌 verifier媛 蹂대뒗 ?꾨뱶??鍮꾩썙 ?붾떎. ?곕씪???щ엺 ?뺤씤 ?꾩뿉???꾨즺 利앷굅濡??듦낵?섏? ?딆쑝硫? `reference-only-not-final-gt` 洹쒖튃怨?manual missed ?꾨낫 row瑜?紐낆떆?쒕떎.
- `scripts/new-yolo-gui-smoke-checklist.ps1`? `scripts/verify-yolo-gui-smoke-state.ps1`瑜?異붽??덈떎. GUI smoke verifier??Home??YOLO ?좏깮/model picker, detector ?좏깮 以꾩쓽 `YOLO ?ㅼ슫濡쒕뱶` 踰꾪듉, startup arg 湲곕컲 smoke preset(`--yolo-smoke`), Workspace ?먮룞 寃異? preview/manual edit/export/state persistence 肄붾뱶 寃쎈줈瑜?source invariant濡??뺤씤?섍퀬, `-RequireManualPass`瑜?遺숈씠硫??섎룞 泥댄겕由ъ뒪?몄쓽 `open-video`, `select-yolo-backend`, `download-yolo-model`, `run-yolo-auto-detect`, `preview-result`, `preview-track-hold`, `manual-edit`, `export`, `reopen-state`媛 紐⑤몢 `status=pass`, `evidenceType`, `artifactPath`, evidence瑜?媛?몄빞 ?듦낵?쒕떎. `preview-track-hold`????踰?紐⑥옄?댄겕 ????곸씠 吏㏃? detector 誘명깘 援ш컙?먯꽌 off/on 源쒕컯???놁씠 ?좎??섎뒗吏 ?뱁솕 利앷굅濡??뺤씤?쒕떎. `artifactPath`???ㅼ젣 ?앹꽦??鍮꾩뼱 ?덉? ?딆? ?뚯씪?댁뼱???섎ŉ, `evidenceType`蹂꾨줈 screenshot? ?대?吏, recording? ?곸긽, log??`.log`/`.txt`, export output? ?곸긽 ?뺤옣?먯뿬???쒕떎.
- `scripts/verify-yolo-startup-smoke-state.ps1`瑜?異붽??덈떎. ??verifier??`AppStartupOptions.Parse("--yolo-smoke --open-manual")`媛 YOLO backend, YOLO5Face, `srcTest/260102_jp_10.mp4`, `.tmp/models/YoloV5Face.onnx`瑜??ㅼ젣 議댁옱?섎뒗 寃쎈줈濡?resolve?섎뒗吏 ?뺤씤?섍퀬, `--open-auto --no-auto-export --frame <index>`泥섎읆 ?먮룞 寃異????뱀젙 preview frame???④린??smoke ?쒖옉 ?듭뀡??寃利앺븳?? `HomePageViewModel.ApplyStartupOptions()`? `MainWindowViewModel` ?앹꽦?먯뿉 ?곸슜?덉쓣 ??`IsYoloDetectorSelected`? `CanStartWorkspace`媛 true?몄?, `AutoExportAfter=false`? startup frame index媛 ?곸슜?섎뒗吏 console harness濡??뺤씤?쒕떎. ?곕씪???섎룞 GUI smoke瑜??쒖옉?섍린 ??preset ?먯껜??寃쎈줈/VM ?명똿 ?ㅻ쪟???먮룞 gate?먯꽌 ?≫엺??
- `scripts/prepare-yolo-gui-smoke-evidence.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??`.tmp/yolo-gui-smoke/evidence` ?대뜑? `.tmp/yolo-gui-smoke/gui-smoke-evidence-guide.md`瑜?留뚮뱾怨? `-UpdateChecklist`瑜?遺숈씠硫?`manual-smoke-checklist.csv`??`artifactPath`留?step蹂?沅뚯옣 寃쎈줈濡?梨꾩슫?? `status=pass`??`evidence`???먮룞?쇰줈 梨꾩슦吏 ?딆쑝誘濡??ㅼ젣 Avalonia GUI ?뺤씤 ?꾩뿉???꾨즺 利앷굅濡??듦낵?섏? ?딅뒗?? `preview-track-hold`??`preview-track-hold.mp4` ?뱁솕 利앷굅瑜??붽뎄?섍퀬, export???ㅼ젣 ?대낫???곸긽 ?뚯씪??媛由ъ폒???쒕떎. evidence guide?먮뒗 9媛?GUI step 媛곴컖?????`set-yolo-gui-smoke-evidence.ps1 -StepId ...` 紐낅졊???앹꽦??罹≪쿂 ???대떦 row留?寃利??낅젰?????덇쾶 ?덈떎.
- `scripts/set-yolo-gui-smoke-evidence.ps1`瑜?異붽??덈떎. ??helper???щ엺??Avalonia GUI?먯꽌 罹≪쿂/?뱁솕/export瑜?留뚮뱺 ???뱀젙 `stepId` row留?`status=pass`濡?梨꾩슫?? ?낅젰??artifact媛 ?ㅼ젣 ?뚯씪?몄?, 鍮꾩뼱 ?덉? ?딆?吏, `evidenceType`怨??뺤옣?먭? 留욌뒗吏, evidence ?ㅻ챸??鍮꾩뼱 ?덉? ?딆?吏瑜?寃?ы븯誘濡?GUI smoke CSV瑜?吏곸젒 ?몄쭛?????앷만 ???덈뒗 ?ㅼ닔瑜?以꾩씤?? ??helper???ㅼ젣 ?붾㈃ ?뺤씤???泥댄븯吏 ?딆쑝硫? 罹≪쿂 ?뚯씪???녿뒗 ?곹깭?먯꽌??row瑜?pass濡?留뚮뱾 ???녿떎.
- ?섎룞 ?뚰겕?ㅽ럹?댁뒪 吏꾩엯?먯꽌 31,996?꾨젅??smoke ?곸긽??timeline thumbnail ?좎깮?깆씠 濡쒕뵫 紐⑤떖???ㅻ옒 ?〓뒗 臾몄젣媛 ?뺤씤?섏뼱, `VideoSession`? 湲곕낯?곸쑝濡?eager thumbnail preload瑜??섏? ?딄퀬 `TimelineFrameStrip`? render 以?FFmpeg seek/decode瑜?吏곸젒 ?ㅽ뻾?섏? ?딅룄濡??섏젙?덈떎. ?녿뒗 ?몃꽕?쇱? cache miss ??諛깃렇?쇱슫?쒕줈 ?붿껌?섍퀬, render??cache hit留?洹몃┛?? 鍮뚮뱶??DLL??`--yolo-smoke --open-manual`濡??ㅽ뻾??15珥??ㅽ겕由곗꺑(`.tmp/yolo-gui-smoke/evidence/open-video-dll-15s.png`)?먯꽌 Manual workspace 吏꾩엯???뺤씤?덈떎.
- `scripts/verify-yolo-gui-smoke-state.ps1 -SelfTest`瑜?異붽??덈떎. ??self-test??synthetic manual checklist? 利앷굅 ?뚯씪??留뚮뱺 ??`-RequireManualPass` 寃쎈줈瑜?洹몃?濡??듦낵?쒖폒, ?섎룞 GUI smoke 利앷굅 寃利?濡쒖쭅??evidence type/?뚯씪 議댁옱/鍮꾩뼱 ?덉? ?딆? artifact/?뺤옣??議곌굔???ㅼ젣濡?寃?ы븯?붿? ?뺤씤?쒕떎. negative self-test 3媛쒕룄 異붽???export step???섎せ??artifact ?뺤옣?? 議댁옱?섏? ?딅뒗 artifact path, `status=fail` ?됱씠 紐⑤몢 ?ㅽ뙣濡??≫엳?붿? ?뺤씤?쒕떎.
- 2026-05-24 GUI smoke ?ш컻 濡쒓렇 諛섏쁺: Visual Studio debug output?먯꽌 `YoloFaceOnnxDetector/GPU:DirectML`, `processed=298`, `detects=298`, `totalMs=10337`, export `frames=300`, `directFaceFrames=269`, `totalMs=14833`???뺤씤?덈떎. ??異쒕젰?쇰줈 `run-yolo-auto-detect` ?④퀎??local ignored checklist/evidence?먯꽌 `pass`濡?湲곕줉?덈떎. 異붿쟻 ???臾몄꽌 `YOLO_GUI_SMOKE_RESULT.md`?먮룄 run log evidence missing ??ぉ??resolved ?곹깭濡?諛붽엥??
- 媛숈? GUI smoke ?ш컻?먯꽌 Spacebar preview ?ъ깮 ??`TaskCanceledException`怨?`[FramePreview] exact frame not available` 濡쒓렇媛 ???諛쒖깮?섎ŉ blurry preview媛 蹂댁씤?ㅻ뒗 ?ъ슜??愿李곗쓣 諛쏆븯?? `FramePreviewViewModel`? ?ъ깮 以?留?tick留덈떎 exact frame load瑜??쒖옉/痍⑥냼?섏? ?딄퀬, 理쒖떊 frame index留?queue???먭퀬 exact frame decode瑜???踰덉뿉 ?섎굹???섑뻾?섎룄濡??섏젙?덈떎. `scripts/verify-yolo-gui-smoke-state.ps1`???댁젣 ??queued playback decode invariant瑜?source check濡??뺤씤?쒕떎.
- 媛숈? preview cancellation 濡쒓렇瑜?以꾩씠湲??꾪빐 `TimelineController`???꾨젅??蹂寃쎈쭏???댁쟾 `CancellationTokenSource`瑜?痍⑥냼?섏? ?딄퀬 request id濡?理쒖떊 exact thumbnail/frame ?붿껌留??곸슜?섍쾶 諛붽엥?? `ExactFrameProvider`??痍⑥냼 ??`OperationCanceledException`???섏?吏 ?딄퀬 `null`??諛섑솚?섎?濡?Visual Studio debug output?????`TaskCanceledException` first-chance 濡쒓렇瑜?以꾩씤??
- YOLO ?ㅼ젙 ?붾㈃??input size/tile numeric control??醫곸븘 `640`??`64`泥섎읆 蹂댁씠??UI 臾몄젣???뺤씤?덈떎. `HomePageView.axaml`?먯꽌 YOLO input size numeric width瑜?`128`, tile columns/rows numeric width瑜?`92`濡??볧삍怨? `verify-yolo-gui-smoke-state.ps1`媛 ?대떦 source invariant瑜??뺤씤?쒕떎.
- `scripts/open-yolo-manual-gates.ps1`瑜?異붽??덈떎. ??helper??full-GT `review-index.html`, `full-gt-review.csv`, `full-frame-review.csv`, GUI `manual-smoke-checklist.csv` 寃쎈줈? ?꾨즺 ??寃利?紐낅졊????踰덉뿉 異쒕젰?쒕떎. `-Open`??遺숈씠硫?由щ럭 ?뚯씪???닿퀬, `-WriteSummary -OpenDashboard`瑜?遺숈씠硫?pending progress? artifact 留곹겕媛 臾띠씤 `.tmp/yolo-manual-gates/manual-gate-dashboard.html`??諛붾줈 ?곕떎. GUI smoke媛 ?⑥븘 ?덉쑝硫?`nextGuiStep`, `nextGuiArtifactPath`, `nextGuiEvidenceSetterCommand`瑜?異쒕젰???ㅼ쓬 ?섎룞 罹≪쿂 ???ㅽ뻾??`set-yolo-gui-smoke-evidence.ps1` 紐낅졊??諛붾줈 ?덈궡?쒕떎. ?먰븳 `dotnet run --project FaceShield.csproj -- --yolo-smoke --open-manual`/`--open-auto --no-auto-export` 紐낅졊??summary???④꺼 `srcTest/260102_jp_10.mp4`? `.tmp/models/YoloV5Face.onnx`瑜??먮룞 ?명똿???곹깭濡??섎룞 smoke瑜??쒖옉?????덇쾶 ?쒕떎. `-VerifyReady`???꾩옱 pending review package媛 以鍮꾨릱?붿? ?뺤씤?쒕떎. ?щ엺???쇰꺼/GUI smoke 利앷굅瑜?梨꾩슫 ??`-VerifyCompleted`瑜?遺숈씠硫?`verify-yolo-manual-readiness-state.ps1 -AllowCompletedFullGt -AllowCompletedGuiSmoke` ?듯빀 寃쎈줈, `verify-yolo-full-gt-reviewed-state.ps1 -RequireFullFrameReview -RequireEvidence -RequireArtifacts`, `verify-yolo-gui-smoke-state.ps1 -RequireManualPass`瑜??곗냽 ?ㅽ뻾?쒕떎.
- `scripts/open-yolo-manual-gates.ps1 -WriteSummary`???꾩옱 ?⑥? gate, 由щ럭 ?곗텧臾?寃쎈줈, full-GT/full-frame/GUI pending row ?? ?꾨즺 ??寃利?紐낅졊, GUI ?꾩닔 step, `preview-track-hold` ?뺤씤 ?≪뀡??`.tmp/yolo-manual-gates/manual-gate-summary.md`??Markdown?쇰줈 ?④릿?? ?숈떆??`.tmp/yolo-manual-gates/manual-gate-dashboard.html`???앹꽦??`review-index.html`, pending report, full-GT CSV, full-frame CSV, GUI checklist, GUI smoke evidence guide瑜?釉뚮씪?곗? 留곹겕? pending progress card濡????붾㈃??臾띔퀬, 泥?pending crop/full-frame/GUI row瑜?`Pending Preview`濡?吏곸젒 蹂댁뿬以?? ?먰븳 AI-assisted candidate CSV 寃쎈줈? human review draft report瑜?`reference-only-not-final-gt` 洹쒖튃怨??④퍡 ?몄텧???щ엺??鍮좊Ⅴ寃??議고븯?? ?대? 理쒖쥌 GT/?꾨즺 洹쇨굅濡??ㅼ씤?섏? ?딄쾶 ?덈떎. GUI smoke checklist媛 ?놁쓣 ?뚮뒗 `-PrepareGuiChecklist`瑜?遺숈뿬 `scripts/new-yolo-gui-smoke-checklist.ps1`濡?pending checklist瑜?留뚮뱾 ???덇퀬, 湲곗〈 checklist????뼱?곗? ?딅뒗?? `-PrepareGuiEvidence`瑜?遺숈씠硫?`scripts/prepare-yolo-gui-smoke-evidence.ps1 -UpdateChecklist -Verify`瑜??ㅽ뻾??checklist??`artifactPath`留?沅뚯옣 evidence 寃쎈줈濡?梨꾩슦怨? pass/evidence???щ엺 ?뺤씤 ?꾧퉴吏 鍮꾩썙 ?붾떎. ?щ엺??full-GT? GUI smoke 利앷굅瑜?梨꾩슫 ?ㅼ뿉??`scripts/complete-yolo-goal-after-manual-gates.ps1 -AllowQualityGateFailure -UpdatePlan -RunYoloState`濡?full-GT/GUI verifier, plan `yolo-goal-audit-state` marker 媛깆떊, `verify-yolo-completion-audit-state.ps1 -RequireComplete`, ?꾨즺 evidence report, 理쒖쥌 `verify-yolo-state.ps1 -RequireComplete`瑜???踰덉뿉 ?ㅽ뻾?????덈떎. ?대븣 full-GT quality gate ?ㅽ뙣??異붿쿇 ?꾨낫 ?놁쓬??洹쇨굅濡??④린硫?YOLO瑜?異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??
- `scripts/verify-yolo-manual-gate-helper-state.ps1`瑜?異붽??덈떎. ??verifier??`open-yolo-manual-gates.ps1 -VerifyReady`媛 ?꾩옱 pending review package?먯꽌 ?듦낵?섎뒗吏 ?뺤씤?섍퀬, `-VerifyCompleted`??CSV ?곹깭瑜??쎌뼱 pending?대㈃ `Review CSV has unreviewed rows`濡??ㅽ뙣?댁빞 ?섎ŉ, ?щ엺??full-GT/GUI 泥댄겕由ъ뒪?몃? 紐⑤몢 completed ?곹깭濡?梨꾩슫 ?ㅼ뿉??媛숈? completed 寃쎈줈媛 ?듦낵?댁빞 ?쒕떎???곹깭 湲곕컲 寃利앹쓣 ?섑뻾?쒕떎. ?먰븳 AI-assisted candidate full-GT CSV? synthetic GUI artifact瑜??ъ슜??completed-path fixture瑜?留뚮뱾??`-VerifyCompleted -MaxMisses 1 -MaxFalsePositives 13 -MaxLowIou 1`???ㅼ젣濡??듦낵 媛?ν븳吏 ?뺤씤?쒕떎. ??fixture??寃利?諛곌? self-test??肉??щ엺???뺤젙??理쒖쥌 GT/GUI 利앷굅媛 ?꾨땲??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1 -RunYoloGuiSmokeState` ?ㅽ뻾 ?깃났. 湲곕낯 FaceONNX gate? GUI smoke source invariant媛 ?④퍡 ?듦낵?덈떎. ?섎룞 泥댄겕由ъ뒪?몃뒗 ?꾩쭅 ?묒꽦?섏? ?딆븯?쇰?濡?`-RequireManualPass`???ㅽ뻾?섏? ?딆븯??
- `scripts/verify-yolo-profile-state.ps1`瑜??뺤옣??YOLO profile ??λ퓧 ?꾨땲??UI/?고???遺꾨━ invariant???뺤씤?쒕떎. ?꾩옱 寃利???ぉ?먮뒗 Home ?붾㈃??detector selector, detector ?좏깮 以꾩쓽 `YOLO ?ㅼ슫濡쒕뱶` 踰꾪듉/吏꾪뻾 ?곹깭, YOLO 紐⑤뜽 醫낅쪟 selector, YOLO 紐⑤뜽 ?뚯씪 picker, YOLO threshold/input/tiling/downscale/tracking/detectEvery/parallel 諛붿씤?? ???곗씠???ㅼ슫濡쒕뱶 寃쎈줈? ?붾（??濡쒖뺄 `Models/Yolo` 湲곕낯 紐⑤뜽 ?먯깋, FaceONNX threshold panel 遺꾨━, FaceONNX backend?먯꽌留?`DetectorAutoTuner`媛 ?몄텧?섎뒗吏, auto-tune 寃곌낵媛 FaceONNX options?먮쭔 諛섏쁺?섎뒗吏, YOLO ?꾩슜 track/filter profile??FaceONNX 湲곕낯 profile怨?遺꾨━?섏뼱 ?덈뒗吏, YOLOv8-Face? YOLO5Face profile??threshold肉??꾨땲??downscale/tracking/detectEvery/parallel session源뚯? ?낅┰ ????곸슜?섎뒗吏, 異붿쟻 toggle??爰쇱졇 ?덉쑝硫?track postprocess? temporal smoothing???곸슜?섏? ?딅뒗吏, track postprocess/ROI/smoothing ?댄썑 ?꾩옱 preview frame???ㅼ떆 ?뚮뜑留곹븯?붿?, smoke harness媛 紐낆떆?곸씤 `-YoloModelPath` ?놁씠 ?붾（??濡쒖뺄 YOLO 紐⑤뜽留뚯쑝濡?YOLO backend瑜??먮룞 ?쒖꽦?뷀븯吏 ?딅뒗吏(`smoke-harness-faceonnx-default=pass`)媛 ?ы븿?쒕떎.
- `YoloFaceOnnxDetector`???낅젰 tensor ?ш린??ONNX input metadata媛 `640x640`泥섎읆 怨좎젙 dimension???쒓났?섎㈃ 紐⑤뜽 metadata 媛믪쓣 ?곗꽑 ?ъ슜?섍퀬, dimension???숈쟻?닿굅??鍮꾩뼱 ?덉쓣 ?뚮쭔 UI/profile??`InputWidth`/`InputHeight` 媛믪쓣 ?ъ슜?쒕떎. ???곹깭?먯꽌 UI ?낅젰 ?ш린媛 736?댁뼱??怨좎젙 640 紐⑤뜽?먮뒗 640 tensor瑜??ｌ뼱 `Got: 736 Expected: 640` ?ㅻ쪟瑜??쇳븳??
- `YoloFaceOnnxDetector`?먮룄 FaceONNX? 媛숈? 諛⑹떇??provider ?곹깭 湲곕줉??異붽??덈떎. `CreateSessionOptions()`濡?ORT option??援ъ꽦?섍퀬, GPU ?붿껌 ??`Microsoft.ML.OnnxRuntime.DirectML`??`AppendExecutionProvider_DML`??李얠븘 遺숈씤?? DirectML provider 異붽? ?먮뒗 GPU session ?앹꽦 ?ㅽ뙣 ??CPU session?쇰줈 fallback?섎ŉ, `GetLastExecutionProviderLabel()`/`GetLastExecutionProviderError()`濡?`GPU:DirectML`, `CPU(媛???ㅽ뙣)` ?곹깭? ?ㅽ뙣 ?먯씤???몄텧?쒕떎. `AutoMaskRunSummary`??detector ?대쫫怨?Home `AutoAccelStatus`??YOLO ?좏깮 ????YOLO provider ?곹깭瑜??ъ슜?쒕떎.
- 怨좎젙 ?낅젰 紐⑤뜽 ?뺤씤: `.tmp/models/yolov8n-face-lindevs.onnx`??input metadata媛 `1x3x640x640`?대떎. ??紐⑤뜽??`-YoloInputSize 736`?쇰줈 ?ㅽ뻾??吏㏃? smoke?먯꽌 shape ?ㅻ쪟 ?놁씠 ?꾨즺?덇퀬, summary detector??`YoloFaceOnnxDetector/GPU:DirectML`濡?湲곕줉?먮떎. ??smoke???낅젰 shape/provider ?곹깭 ?뺤씤?⑹씠硫? YOLOv8n ?덉쭏 異붿쿇 洹쇨굅濡??ъ슜?섏? ?딅뒗??
- YOLO 源쒕컯??諛⑹? tracking 蹂닿컯: FaceONNX 湲곕낯 track profile? ?좎??섍퀬, YOLO profile?먯꽌 ?뺤젙 track??`MaxLostFillFrames`瑜?3?먯꽌 6?쇰줈 ?섎졇?? 異붽?濡?媛숈? track?쇰줈 ?댁뼱議뚯?留??대? no-face gap??湲곕낯 `MaxFillGap=5`瑜??섎뒗 寃쎌슦?먮룄 ?뺤젙 track?대㈃ `MaxConfirmedTrackHoldFrames=8`源뚯? 蹂닿컙/hold?섎룄濡??덈떎. 利?YOLO媛 媛숈? ?쇨뎬???쇱젙 ?꾨젅???댁긽 ?≪? ???좉퉸 誘명깘?섎뜑?쇰룄 ?덉륫/蹂닿컙 諛뺤뒪濡?紐⑥옄?댄겕瑜????ㅻ옒 ?좎??쒕떎. FaceONNX 湲곕낯 寃쎈줈?먯꽌???묒? 以묒븰 ?꾨낫媛 ?앸궃 ?ㅺ퉴吏 ?붿긽?쇰줈 ?섏뼱?섏? ?딄쾶 small-track lost-fill??湲곕낯 李⑤떒?섍퀬, YOLO profile?먯꽌留?`AllowSmallTrackLostFill=true`濡?紐낆떆?덈떎. ?⑤컻 ?ㅽ깘???ㅻ옒 ?⑤뒗 寃껋쓣 留됯린 ?꾪빐 `ConfirmedTrackMinDetections`, short-track ?쒓굅, lower-frame ?쒓굅 議곌굔? ?좎??쒕떎. smoke harness??YOLO track profile??媛숈? `MaxLostFillFrames=6`, `MaxConfirmedTrackHoldFrames=8`, `AllowSmallTrackLostFill=true`瑜??ъ슜?쒕떎.
- YOLO lost-fill 6?꾨젅???곸슜 ??6遺?3珥????gate??strict frame-match 湲곗??쇰줈??`baselineFrames=19`, `optimizedFrames=20`, `onlyBaseline=0`, `onlyOptimized=1(frame 9)`, `boxCountDiffFrames=2`媛 ?먮떎. frame 9 crop `.tmp/yolo-crops/test-0900-yolo5face/onlyOptimized-optimized-frame-000009-00.png`?먮뒗 ?놁뼹援?留덉뒪?ш? 蹂댁씠誘濡???李⑥씠???⑥닚 ?ㅽ깘 利앷?媛 ?꾨땲??FaceONNX 湲곗??좎씠 鍮좊쑉由??쇨뎬 ?꾨젅?꾩쓣 YOLO track hold媛 ?좎???耳?댁뒪濡?遺꾨쪟?쒕떎. 怨듯넻 ?꾨젅???덉쭏? `avgBestIou=0.971`, `minBestIou=0.944`濡??좎??먭퀬 YOLO ?꾩쿂由щ뒗 `lostFilled=6`, `lostFrames=6,7,8,9,10,11`??湲곕줉?덈떎. ?곕씪??`verify-yolo-representative-gate.ps1`??`-AllowFrameMismatch`? ??吏?쒕? 湲곗??쇰줈 源쒕컯??諛⑹? tracking ?숈옉???뺤씤?쒕떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-representative-gate.ps1` ?ㅽ뻾 ?깃났. 理쒖떊 ?ㅽ뻾?먯꽌 `baselineFrames=19`, `optimizedFrames=20`, `onlyBaseline=0`, `onlyOptimized=1(frame 9)`, `avgBestIou=0.971`, `minBestIou=0.944`, `boxCountDiffFrames=2`, `SmokeQualityGate passed=True`?怨?YOLO ?꾩쿂由щ뒗 `lostFilled=6`, `lostFrames=6,7,8,9,10,11`?댁뿀??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-extended-gate.ps1` ?ㅽ뻾 ?깃났. ???ㅽ겕由쏀듃???대? smoke??exit code 2瑜?湲곕?媛믪쑝濡?蹂몃떎. 理쒖떊 ?ㅽ뻾?먯꽌 FaceONNX baseline ?먮룞 寃異쒖? `totalMs=325,212ms`, YOLO optimized ?먮룞 寃異쒖? `totalMs=119,403ms`?吏留? A/B??`baselineFrames=83`, `optimizedFrames=81`, `onlyBaseline=13`, `onlyOptimized=11`, `avgBestIou=0.770`, `minBestIou=0.000`, `avgBaselineCoverage=0.868`, `boxCountDiffFrames=15`, YOLO `lostFilled=24`, `SmokeQualityGate passed=False`???꾩옱 YOLO5Face profile??理쒖쥌 異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-extended-export-gate.ps1` ?ㅽ뻾 ?깃났. ???ㅽ겕由쏀듃???대? smoke??exit code 2瑜?湲곕?媛믪쑝濡?蹂몃떎. 理쒖떊 ?ㅽ뻾?먯꽌 FaceONNX baseline? ?먮룞 寃異?`totalMs=325,178ms`, export `totalMs=41,707ms`, `directFaceFrames=83`?댁뿀怨? YOLO optimized???먮룞 寃異?`totalMs=119,854ms`, export `totalMs=41,437ms`, `directFaceFrames=81`??? export???꾨즺?먯?留?A/B??`baselineFrames=83`, `optimizedFrames=81`, `onlyBaseline=13`, `onlyOptimized=11`, `boxCountDiffFrames=15`, `SmokeQualityGate passed=False`??detector 援먯껜留뚯쑝濡???30珥?援ш컙??異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??
- `scripts/run-yolo-ten-minute-full.ps1`瑜?異붽??덈떎. ??runner???먮낯 `srcTest/260102_jp_10.mp4`?먯꽌 10遺?clip `.tmp/srcTest-smoke/smoke-0200-600s.mp4`瑜?以鍮꾪븯怨? ?꾩옱 YOLO5Face 珥덇린 profile(`objectness=0.12`, `confidence=0.18`, `nms=0.45`, `InputSize=640`)濡?10遺꾧툒 ?먮룞 寃異?export瑜??ㅽ뻾?쒕떎. 湲곕낯? YOLO optimized ?⑤룆?대ŉ, `-RunBaseline`??遺숈씠硫?FaceONNX baseline A/B源뚯? ?ы븿?섍퀬 `-AllowQualityFailure`濡?湲?A/B ?ㅽ뙣 濡쒓렇瑜?蹂댁〈?????덈떎. `-BaselineOnly`瑜?遺숈씠硫?YOLO 紐⑤뜽 ?놁씠 FaceONNX baseline留??ㅽ뻾??湲?10遺?baseline ?쒓컙??蹂꾨룄濡??뺣낫?????덈떎.
- `scripts/run-yolo-ten-minute-full.ps1`??湲?FaceONNX baseline/A-B ?ㅽ뻾??以묎컙 ?곹깭瑜?蹂????덈룄濡?smoke 異쒕젰??利됱떆 肄섏넄怨?濡쒓렇 ?뚯씪???숈떆???대떎. ?좉퇋 ?ㅽ뻾 濡쒓렇??`yolo-ten-minute-yolo-only-*`, `yolo-ten-minute-baseline-only-*`, `yolo-ten-minute-ab-*`泥섎읆 紐⑤뱶蹂??뚯씪紐낆쑝濡?遺꾨━?쒕떎.
- `scripts/run-yolo-ten-minute-full.ps1`??`-DumpDetections`, `-DumpCompareDetails`, `-DumpCompareOverlays`, `-DumpCompareCrops` ?꾨떖 ?듭뀡??異붽??덈떎. 湲?10遺??ㅽ뻾?먯꽌??`[SmokeDetection]` prediction log? compare overlay/crop ?먮즺瑜??④꺼 `new-yolo-full-gt-template.ps1`濡?full GT ?쇰꺼留?CSV瑜?留뚮뱾 ???덈떎.
- `scripts/verify-yolo-ten-minute-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??10遺?runner, ?먮낯 ?곸긽, 10遺?clip 以鍮??곹깭, runner??incremental log streaming, 洹몃━怨?臾몄꽌??10遺?寃利?誘몄셿猷??곹깭瑜??뺤씤?쒕떎. `-RequireClip`??遺숈씠硫?`.tmp/srcTest-smoke/smoke-0200-600s.mp4`媛 ?ㅼ젣 以鍮꾨릺???덇퀬 1GB ?댁긽?몄? 寃?ы븳?? `-RequireBaselineOnlyRun`??遺숈씠硫?理쒖떊 `yolo-ten-minute-baseline-only-*.log`媛 FaceONNX baseline留??ㅽ뻾?덇퀬 YOLO/optimized case媛 ?욎씠吏 ?딆븯?붿? ?뺤씤?쒕떎. `-RequireIncompleteBaselineFullAttempt`瑜?遺숈씠硫?以묐떒??10遺?FaceONNX baseline-only full 濡쒓렇媛 baseline-only/pipe-single 寃쎈줈?怨? `[YoloTenMinuteFull] complete` ?놁씠 `AutoMaskPipe frames=240` ?섏?源뚯?留?吏꾪뻾??誘몄셿猷??쒕룄?몄? ?뺤씤?쒕떎.
- `scripts/verify-yolo-ten-minute-state.ps1 -RequireRun`? YOLO 10遺?optimized-only 濡쒓렇? export ?뚯씪 議댁옱/?ш린肉??꾨땲??`ffprobe`濡?異쒕젰 ?곸긽??video stream??寃利앺븳?? ?꾩옱 湲곕?媛믪? `3840x2160`, `nb_frames >= 17980`, duration `599~601s`?대떎. ?대뒗 YOLO ?⑤룆 10遺?export artifact 臾닿껐??寃利앹씠硫? FaceONNX 10遺?A/B ?꾨즺??full GT ?덉쭏 寃利앹쓣 ?泥댄븯吏 ?딅뒗??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-yolo-ten-minute-full.ps1 -SkipClipPrepare` ?ㅽ뻾 ?깃났. 10遺?clip `.tmp/srcTest-smoke/smoke-0200-600s.mp4`?먯꽌 YOLO5Face `0.12/0.18/0.45`, CPU, `ParallelDetectorCount=2`, baseline ?놁씠 optimized ?⑤룆 ?먮룞 寃異?export瑜??ㅽ뻾?덈떎. 濡쒓렇??`.tmp/yolo-ten-minute/yolo-ten-minute-20260523-000044.log`?닿퀬 異쒕젰? `.tmp/srcTest-smoke/smoke-0200-600s_blur.mp4`??
- 10遺?YOLO optimized ?⑤룆 ?먮룞 寃異?寃곌낵: `detector=YoloFaceOnnxDetector`, `mode=pipe-parallel`, `totalFrames=17984`, `processed=17982`, `detects=17982`, `interpolated=0`, `decodeMs=1,647,657`, `detectMs=5,058,207`, `totalMs=2,536,529`, filter `regular=15053`, `small=15862`, `rejected=19146`. Track/ROI ?꾩쿂由щ뒗 `tracks=2644`, `filled=5492`, `lostFilled=1762`, `removedShort=686`, `removedLower=13`, `rewritten=8064`, ROI `attempts=32`, `hits=6`, `elapsedMs=10,612`???
- 10遺?YOLO optimized ?⑤룆 export 寃곌낵: `[ExportRunSummary]` 湲곗? `frames=17984`, `bitmapMaskFrames=0`, `directFaceFrames=8063`, `swsToBgraMs=82,579`, `maskMs=439,442`, `swsToEncMs=203,111`, `encodeMs=59,806`, `totalMs=1,375,350`?댁뿀?? 異쒕젰 ?뚯씪? `ffprobe` 湲곗? `3840x2160`, `30000/1001fps`, `nb_frames=17983`, `duration=600.032767`, size `1,490,083,950` bytes??
- ??10遺??ㅽ뻾? YOLO optimized ?⑤룆 end-to-end ?쒓컙 痢≪젙?대ŉ, FaceONNX 10遺?baseline A/B??label 湲곕컲 GT ?덉쭏 寃利앹? ?꾨땲?? ?대? 30珥??뺤옣 gate媛 ?ㅽ뙣?덉쑝誘濡???痢≪젙留뚯쑝濡?YOLO5Face瑜?異붿쿇 ?꾨낫濡??밴꺽?섏? ?딅뒗??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-yolo-ten-minute-full.ps1 -SkipClipPrepare -BaselineOnly -LogDir .tmp/yolo-ten-minute-baseline-full`濡?10遺?FaceONNX baseline-only ?꾩껜 ?ㅽ뻾???쒕룄?덉?留? `pipe-single`?먯꽌 ??240?꾨젅??泥섎━??`totalMs=108,278` ?섏??쇰줈 吏꾪뻾?섏뼱 ?꾩껜 17,984?꾨젅???꾨즺?먮뒗 ?μ떆媛꾩씠 ?꾩슂?섎떎怨??먮떒?섍퀬 以묐떒?덈떎. 濡쒓렇??`.tmp/yolo-ten-minute-baseline-full/yolo-ten-minute-baseline-only-20260523-032108.log`?대ŉ, `[YoloTenMinuteFull] complete`媛 ?놁쑝誘濡??꾨즺 artifact濡?蹂댁? ?딅뒗??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-state.ps1 -RunRepresentativeGate` ?ㅽ뻾 ?깃났.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-yolo-state.ps1 -RunTenMinuteState -RequireTenMinuteClip -RequireTenMinuteRun` ?ㅽ뻾 ?깃났. YOLO profile/crop review/?쒕낯 GT label/conclusion/distribution/goal audit/10遺?run artifact ?곹깭瑜???踰덉뿉 ?뺤씤?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-yolo-ten-minute-full.ps1 -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -Clip .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipClipPrepare -SkipExport -LogDir .tmp/yolo-ten-minute-smoke` ?ㅽ뻾 ?깃났. 10遺?runner??incremental log streaming 寃쎈줈瑜?吏㏃? 3珥?clip?쇰줈 ?뺤씤?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-yolo-ten-minute-full.ps1 -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -Clip .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipClipPrepare -SkipExport -BaselineOnly -LogDir .tmp/yolo-ten-minute-baseline-smoke` ?ㅽ뻾 ?깃났. 10遺?runner??FaceONNX baseline-only 寃쎈줈媛 YOLO 紐⑤뜽 ?놁씠 ?ㅽ뻾?섍퀬 optimized case瑜??앸왂?섎뒗 寃껋쓣 吏㏃? 3珥?clip?쇰줈 ?뺤씤?덈떎.
- `scripts/run-yolo-ten-minute-full.ps1 -FaceOnnxOptimizedOnly`瑜?異붽??덈떎. ??紐⑤뱶??YOLO 紐⑤뜽 ?놁씠 `run-srcTest-smoke.ps1`??optimized FaceONNX CPU parallel 寃쎈줈留??ㅽ뻾??湲?baseline-only ?⑥씪 detector蹂대떎 鍮좊Ⅸ FaceONNX optimized-only 痢≪젙 寃쎈줈瑜??쒓났?쒕떎. ?대뒗 理쒖쥌 10遺?A/B baseline ?泥닿? ?꾨땲?? FaceONNX optimized detector ?먯껜??吏㏃? smoke/?쒓컙 鍮꾧탳??寃쎈줈??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-yolo-ten-minute-full.ps1 -Source .tmp/srcTest-smoke/smoke-0600-3s.mp4 -Clip .tmp/srcTest-smoke/smoke-0600-3s.mp4 -SkipClipPrepare -SkipExport -FaceOnnxOptimizedOnly -LogDir .tmp/yolo-ten-minute-faceonnx-optimized-smoke` ?ㅽ뻾 ?깃났. 濡쒓렇??`yolo-ten-minute-faceonnx-optimized-only-*` ?⑦꽩?대ŉ `FaceOnnxDetector/CPU`, `mode=pipe-parallel`, `totalFrames=90` ?댁긽, YOLO detector ?놁쓬, baseline case ?놁쓬 議곌굔??verifier?먯꽌 ?뺤씤?쒕떎.
- `scripts/run-yolo-partial-speed-compare.ps1`瑜?異붽??덈떎. ??wrapper??媛숈? partial clip??以鍮꾪븳 ??YOLO optimized-only? FaceONNX optimized-only瑜??곗냽 ?ㅽ뻾?섍퀬 `[AutoRunSummary]`??`totalMs`, frame ?? log path瑜???以?summary濡?異쒕젰?쒕떎. ?대뒗 10遺??꾩껜 A/B ?꾨즺 洹쇨굅媛 ?꾨땲?? ?꾩껜 10遺?FaceONNX baseline???μ떆媛꾩쑝濡?以묐떒???곹깭?먯꽌 媛숈? clip 湲몄씠??detector ?띾룄 鍮꾧탳瑜??ы쁽 媛?ν븯寃??④린湲??꾪븳 以묎컙 痢≪젙 寃쎈줈??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/run-yolo-partial-speed-compare.ps1 -Seconds 3 -ForcePrepare` ?ㅽ뻾 ?깃났. `.tmp/srcTest-smoke/smoke-0200-partial-speed-3s.mp4`?먯꽌 YOLO optimized-only? FaceONNX optimized-only瑜?媛숈? 3珥?援ш컙?쇰줈 ?ㅽ뻾?덈떎. YOLO??`totalFrames=93`, `processed=90`, `totalMs=20,720`, `faceMaskFrames=6`?댁뿀怨? FaceONNX optimized-only??`totalFrames=93`, `processed=90`, `totalMs=34,039`, `faceMaskFrames=5`??? ??partial 援ш컙??FaceONNX/YOLO ?쒓컙 鍮꾩쑉? `1.643`?댁?留? ?덉쭏 GT媛 ?꾨땲誘濡?異붿쿇 洹쇨굅濡??ъ슜?섏? ?딅뒗??
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1 -RunYoloState -RunYoloRepresentativeGate` ?ㅽ뻾 ?깃났. 湲곕낯 FaceONNX gate? YOLO wrapper/???gate媛 紐⑤몢 ?듦낵?덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1 -RunYoloTenMinuteState -RequireYoloTenMinuteClip -RequireYoloTenMinuteRun` ?ㅽ뻾 ?깃났. 湲곕낯 FaceONNX gate? 10遺?YOLO run artifact ?곹깭媛 紐⑤몢 ?듦낵?덈떎.
- 媛숈? ?곹깭?먯꽌 `dotnet build FaceShield.sln`? ?깃났?덇퀬 湲곗〈 FFmpeg obsolete warning 7媛쒕쭔 異쒕젰?먮떎.
- `scripts/verify-yolo-manual-readiness-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃???⑥? ?섎룞 gate瑜??꾨즺濡?媛꾩＜?섏? ?딄퀬, full-GT review package??鍮??쇰꺼 CSV/crop/frame/overlay artifact, AI-assisted candidate CSV, GUI manual checklist, 10遺?YOLO output/log, 誘몄셿猷?FaceONNX baseline full attempt log媛 ?ㅼ쓬 ?섎룞 寃利앹쓣 吏꾪뻾?????덈뒗 ?곹깭?몄? ?뺤씤?쒕떎. ?ㅼ젣 full-GT ?쇰꺼 ?먮뒗 GUI smoke 利앷굅媛 梨꾩썙吏??ㅼ뿉??`-AllowCompletedFullGt`, `-AllowCompletedGuiSmoke`濡??꾨즺???섎룞 ?뚯씪??媛숈? 寃쎈줈?먯꽌 留됲엳吏 ?딄쾶 寃利앺븷 ???덈떎. ?꾨즺??full-GT ?뚯씪? `verify-yolo-full-gt-reviewed-state.ps1 -RequireFullFrameReview -RequireArtifacts -RequireEvidence`? `FullGtMaxMisses/FullGtMaxFalsePositives/FullGtMaxLowIou` 湲곗??쇰줈 ?ㅼ떆 寃利앺븳??
- `scripts/verify-yolo-completion-audit-state.ps1`瑜?異붽??덈떎. 湲곕낯 紐⑤뱶??plan ?꾩껜 ?띿뒪?멸? ?꾨땲??`yolo-goal-audit-state` marker留??뚯떛?댁꽌 `complete=false`? ?⑥? gate(`remaining=full-gt-label,gui-smoke` ?먮뒗 `remaining=gui-smoke`), `track-hold-state=pass`瑜??뺤씤?섍퀬, ?ㅼ젣 pending CSV ?곹깭, `.tmp/yolo-manual-gates/manual-gate-summary.md`???⑥? gate/?꾨즺 紐낅졊/`preview-track-hold` step???④퍡 ?뺤씤??紐⑺몴媛 ?꾩쭅 ?꾨즺?섏? ?딆븯?뚯쓣 紐낆떆?곸쑝濡?寃利앺븳?? full-GT? GUI smoke 利앷굅媛 梨꾩썙吏??ㅼ뿉??`-RequireComplete`濡?marker??`complete=true`, `remaining=none`, full-GT reviewed gate, GUI `-RequireManualPass`瑜??④퍡 寃?ы븳?? `-AllowQualityGateFailure`瑜??④퍡 遺숈씠硫?full-GT quality gate ?ㅽ뙣瑜?異붿쿇 ?꾨낫 ?놁쓬???꾨즺 利앷굅濡??덉슜?쒕떎. `-SelfTest`??synthetic complete plan/full-GT/GUI fixture瑜?留뚮뱾怨?`-RequireComplete -PredictionCsv`濡??먭린 ?먯떊???ㅼ떆 ?ㅽ뻾??CSV prediction 湲곕컲 ?꾨즺 audit 寃쎈줈瑜??ㅼ젣濡?寃利앺븳?? ?먰븳 plan 蹂몃Ц??`complete=true`/`remaining=none`???덉뼱??marker媛 `complete=false`?대㈃ `-RequireComplete`媛 ?ㅽ뙣?섎뒗 marker-only negative self-test?, plan marker留?`complete=true`濡??섎せ 諛붾뚭퀬 ?ㅼ젣 full-GT/GUI evidence媛 pending?대㈃ `-RequireComplete`媛 ?ㅽ뙣?섎뒗 pending-evidence negative self-test瑜??ㅽ뻾?쒕떎.
- `scripts/open-yolo-manual-gates.ps1 -OpenApp`瑜?異붽??덈떎. ?섎룞 GUI smoke ?섑뻾?먮뒗 媛숈? helper?먯꽌 review/checklist artifact瑜?`-Open`?쇰줈 ?닿퀬, `-OpenApp`?쇰줈 `dotnet run --project FaceShield.csproj`瑜?蹂꾨룄 ?꾨줈?몄뒪濡??꾩썙 `manual-smoke-checklist.csv`??open/preview/track-hold/manual-edit/export/reopen ?④퀎 利앷굅瑜?梨꾩슱 ???덈떎.
- `scripts/verify-auto-mosaic-default.ps1 -RunYoloState`???섏쐞 verifier ?몄옄 ?꾨떖??蹂닿컯?덈떎. YOLO representative/extended model path媛 鍮꾩뼱 ?덉쓣 ?뚮뒗 `verify-yolo-state.ps1`??鍮?model-path ?몄옄瑜??섍린吏 ?딆븘 湲곕낯 FaceONNX 寃利앷낵 YOLO ?곹깭 wrapper媛 媛숈? ?곸쐞 寃利앹뿉???④퍡 ?ㅽ뻾?????덈떎.
- `scripts/verify-auto-mosaic-default.ps1 -RunYoloManualGateSummary`瑜?異붽??덈떎. 湲곕낯 FaceONNX gate瑜??좎???梨?`open-yolo-manual-gates.ps1 -WriteSummary`瑜??몄텧???섎룞 full-GT/GUI smoke gate ?붿빟 ?뚯씪 ?앹꽦源뚯? ?곸쐞 寃利앹뿉???뺤씤?????덈떎.
- `scripts/verify-yolo-sweep-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??紐⑺몴??YOLO ?꾨낫蹂?threshold/objectness/confidence/NMS, tiling, track post-process, box refine, FaceONNX ROI verifier, low-confidence/small-area filter sweep harness? 臾몄꽌?붾맂 ?ㅽ뙣/蹂대쪟 寃곕줎??怨꾩냽 ?⑥븘 ?덈뒗吏 source invariant濡??뺤씤?쒕떎. ?ㅼ젣 sweep ?ㅽ뻾 寃곌낵瑜??덈줈 留뚮뱾吏???딄퀬, sweep ?꾧뎄? 湲곕줉??寃곕줎???꾨씫?섏뼱 YOLO 異붿쿇 ?먮떒 洹쇨굅媛 ?쏀빐吏??寃껋쓣 留됰뒗??
- `scripts/verify-yolo-ready-for-human-gates-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??`verify-yolo-state.ps1`, `open-yolo-manual-gates.ps1 -WriteSummary -VerifyReady`, `verify-yolo-completion-audit-state.ps1`瑜??곗냽 ?ㅽ뻾???먮룞?쇰줈 ?뺤씤 媛?ν븳 YOLO backend/profile/sweep/track/full-GT harness/GUI harness/completion-audit ?곹깭媛 ?듦낵?섎뒗吏 ?뺤씤?쒕떎. ?꾩옱 full-GT ?쇰꺼 寃?섎뒗 ?꾨즺?섏뼱 ?⑥? gate???ㅼ젣 ?щ엺 ?먮떒???꾩슂??`gui-smoke`肉먯씠?? ?곸쐞 湲곕낯 寃利앹뿉?쒕뒗 `verify-auto-mosaic-default.ps1 -RunYoloReadyForHumanGatesState`濡??몄텧?????덈떎.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-auto-mosaic-default.ps1 -RunYoloReadyForHumanGatesState` ?ㅽ뻾 ?깃났. ???ㅽ뻾? 湲곕낯 FaceONNX track/quality/ROI/auto-tune gate瑜?癒쇱? ?듦낵????YOLO ready wrapper源뚯? ?몄텧?덇퀬, 理쒖쥌 ?곹깭??`ready=true`, `remaining=gui-smoke`??? FFmpeg hardware format probe 寃쎄퀬??stderr??異쒕젰?먯?留??대떦 gate?ㅼ? ?듦낵?덈떎.
- `scripts/write-yolo-goal-evidence-report.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??plan marker, manual gate summary, full-GT review CSV, full-frame review CSV, GUI smoke checklist瑜??쎌뼱 `.tmp/yolo-manual-gates/goal-evidence-report.md`???붽뎄?ы빆蹂?evidence/status ?쒕? ?앹꽦?쒕떎. ?꾩옱 ?곹깭?먯꽌??full-GT label review??梨꾩썙議뚭퀬 quality gate??`fail-documented`濡?湲곕줉?섎ŉ, `Avalonia GUI smoke`? `Preview track-hold GUI evidence`???꾩쭅 `pending-human`, goal completion? `incomplete`濡??⑤뒗?? track hold ?뚭퀬由ъ쬁 寃利?`track-hold-state=pass`)怨??ㅼ젣 preview ?뱁솕 利앷굅(`preview-track-hold`)瑜?遺꾨━???щ엺???뺤씤?댁빞 ?섎뒗 源쒕컯??利앷굅媛 ?먮룞 verifier ?듦낵濡??泥대릺吏 ?딄쾶 ?쒕떎. ?먰븳 蹂닿퀬?쒖뿉??`YOLOv8 candidate A/B comparison`, `YOLO5Face candidate A/B comparison`, `Failure-axis classification` ?됱쓣 ?ы븿??理쒖쥌 媛먯궗 ???꾨낫蹂?鍮꾧탳? ?ㅽ뙣 異?臾몄꽌?붽? 鍮좎?吏 ?딄쾶 ?쒕떎. ?щ엺??GUI 利앷굅源뚯? 紐⑤몢 梨꾩슫 ?ㅼ뿉??媛숈? ?ㅽ겕由쏀듃媛 `fullGtQualityGate=pass|fail-documented|fail-blocking|pending-human` ?곹깭瑜?怨꾩궛?섍퀬, `-AllowQualityGateFailure`媛 ?덈뒗 ?ㅽ뙣??`fail-documented`濡?湲곕줉??YOLO 異붿쿇 ?꾨낫 ?놁쓬???꾨즺 利앷굅濡??④릿?? ?대줈???먮룞 gate ?듦낵? ?ㅼ젣 ?꾨즺 ?꾨낫 ?곹깭, 洹몃━怨?臾몄꽌?붾맂 YOLO ?ㅽ뙣 ?곹깭瑜?遺꾨━?쒕떎.
- `scripts/write-yolo-manual-pending-report.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??full-GT crop review, full-frame review, GUI smoke checklist??誘몄엯??row瑜?`.tmp/yolo-manual-gates/manual-pending-report.md`??紐⑥븘 `fullGtPendingRows`, `fullFramePendingRows`, `guiPendingRows`? 泥?pending row 紐⑸줉??湲곕줉?쒕떎. 蹂닿퀬?쒖뿉??`label=face/nonface`, `reviewStatus=pass`, `missedFaceCount`/`missedFaceRowsAdded` ?쇱튂 議곌굔, GUI `status=pass`, `preview-track-hold` recording 利앷굅 議곌굔???④퍡 ?곸뼱 ?섎룞 ?낅젰 ?ㅼ닔瑜?以꾩씤??
- `scripts/complete-yolo-goal-after-manual-gates.ps1`瑜?異붽??덈떎. ??finalizer???섎룞 full-GT/GUI smoke 利앷굅媛 紐⑤몢 梨꾩썙吏???full-GT verifier? GUI `-RequireManualPass`瑜?癒쇱? ?듦낵?댁빞留?`yolo-goal-audit-state`瑜?`complete=true`, `remaining=none`, `completion-audit=pass-complete`濡?諛붽씔?? `-AllowQualityGateFailure`瑜?遺숈씠硫?full-GT quality gate ?ㅽ뙣瑜??ㅽ뙣 ?꾨낫 臾몄꽌??寃쎈줈濡??덉슜?섍퀬, ?댄썑 completion audit怨??꾨즺 evidence report瑜??ㅼ떆 ?앹꽦?쒕떎. `-SelfTest`??synthetic ?꾨즺 fixture濡?marker 媛깆떊怨??꾨즺 report ?앹꽦 寃쎈줈瑜?寃利앺븳??
- `scripts/verify-yolo-completion-finalizer-state.ps1`瑜?異붽??덈떎. ???ㅽ겕由쏀듃??finalizer??strict full-GT/GUI/completion-audit/evidence-report 寃쎈줈? self-test瑜??뺤씤?섍퀬, ?꾩옱 ?ㅼ젣 pending review/checklist ?뚯씪?먯꽌??finalizer媛 `Review CSV has unreviewed rows` 怨꾩뿴 ?ㅻ쪟濡??꾨즺?섏? ?딅뒗吏 negative ?곹깭瑜?寃利앺븳?? ?щ엺??利앷굅瑜?紐⑤몢 梨꾩슫 ?ㅼ뿉??媛숈? ?ㅽ겕由쏀듃媛 finalizer dry-run ?깃났 ?щ?瑜??뺤씤?쒕떎.

<!-- yolo-ten-minute-runner-state: prepared=true; runner=scripts/run-yolo-ten-minute-full.ps1; clip=.tmp/srcTest-smoke/smoke-0200-600s.mp4; profile=Yolo5Face-0.12-0.18-0.45-640; full-run=yolo-optimized-only-pass; output-probe=pass-3840x2160-17980frames-599to601s; baseline-only-runner=short-smoke-pass; baseline-only-full=attempted-incomplete-slow; baseline-only-full-progress=240frames-no-complete; baseline-only-log-pattern=.tmp/yolo-ten-minute-baseline-smoke/yolo-ten-minute-baseline-only-*.log; faceonnx-optimized-only-runner=short-smoke-pass; faceonnx-optimized-only-log-pattern=.tmp/yolo-ten-minute-faceonnx-optimized-smoke/yolo-ten-minute-faceonnx-optimized-only-*.log; partial-speed-compare=short-smoke-pass; partial-yolo-totalMs=20720; partial-faceonnx-totalMs=34039; partial-faceonnx-yolo-ratio=1.643; partial-speed-log-pattern=.tmp/yolo-partial-speed/*/yolo-ten-minute-*.log; log=.tmp/yolo-ten-minute/yolo-ten-minute-20260523-000044.log; autoTotalMs=2536529; exportTotalMs=1375350; directFaceFrames=8063 -->
<!-- yolo-sweep-harness-state: verifier=scripts/verify-yolo-sweep-state.ps1; threshold-sweep=scripts/run-yolo-threshold-sweep.ps1; review-filter-sweep=scripts/run-yolo-review-filter-sweep.ps1; review-filter-candidates=scripts/find-yolo-review-filter-candidates.ps1; axes=objectness,confidence,nms,input,tiling,track,box-refine,faceonnx-roi,low-confidence-filter,small-area-filter; metrics=totalMs,baselineFrames,optimizedFrames,onlyBaseline,onlyOptimized,avgBestIou,minBestIou,avgBaselineCoverage,minBaselineCoverage,boxCountDiffFrames,strictGatePassed; documented-results=yolo5face-thresholds,yolo5face-tiling,yolo5face-roi,yolo5face-box-refine,yolov8s-fail,yolov8l-fail; conclusion=no-final-yolo-recommendation -->
<!-- yolo-gt-label-sample-state: verifier=scripts/verify-yolo-gt-label-state.ps1; passRows=15; passYoloTP=15; passYoloFP=0; passYoloMiss=0; passFaceOnnxFP=0; failRows=26; failUnclear=1; failYoloTP=1; failYoloFP=10; failYoloMiss=0; failFaceOnnxFP=14; scope=sample-crops-not-full-video-gt -->
<!-- yolo-profile-state: verifier=scripts/verify-yolo-profile-state.ps1; settings-version=6; model-profile=YoloV8+Yolo5; detector-profile=threshold,nms,input,tiling; auto-pipeline-profile=downscale,quality,tracking,detectEvery,parallel; faceonnx-default-preserved=pass; apply-profile-restart-suppressed=pass -->
<!-- yolo-full-gt-label-harness-state: verifier=scripts/verify-yolo-full-gt-label-state.ps1; template=scripts/new-yolo-full-gt-template.ps1; template-verifier=scripts/verify-yolo-full-gt-template-state.ps1; review-package=scripts/new-yolo-full-gt-review-package.ps1; review-package-verifier=scripts/verify-yolo-full-gt-review-package-state.ps1; reviewed-verifier=scripts/verify-yolo-full-gt-reviewed-state.ps1; candidate-verifier=scripts/verify-yolo-full-gt-reviewed-candidate-state.ps1; human-review-draft=scripts/new-yolo-human-review-draft.ps1; manual-gate-helper=scripts/open-yolo-manual-gates.ps1; manual-gate-helper-verifier=scripts/verify-yolo-manual-gate-helper-state.ps1; manual-gate-helper-completed-mode=pass; manual-gate-helper-completed-fixture=pass-not-final-gt; mode=selftest-pass-and-synthetic-data-pass; gt-data=missing; prediction-input=csv-or-smokedetection-log; runner-dump=detections-and-compare-artifacts; real-log-template=pass-20-rows; review-package-smoke=pass-20-crops; review-package-no-clobber=pass; review-package-force-regenerate=explicit; review-index-refresh=pass; full-frame-review-smoke=pass-19-candidate-frames; full-frame-overlay=pass; full-frame-candidate-summary=pass; review-index=pass; review-index-input-rules=pass; review-index-csv-key=pass; review-index-pending-fields=pass; reviewed-gate=selftest-pass; manual-missed-consistency=pass; review-artifact-validation=pass; negative-selftests=pass; ai-reviewed-candidate=tp7-fp13-miss1-strict-fail; human-review-draft-safe=pass-reference-only; quality-gate-failure-allowed=pass; real-reviewed-gate=requires-full-frame-review; metrics=tp,miss,false-positive,low-iou -->
<!-- yolo-gui-smoke-harness-state: verifier=scripts/verify-yolo-gui-smoke-state.ps1; startup-verifier=scripts/verify-yolo-startup-smoke-state.ps1; checklist=scripts/new-yolo-gui-smoke-checklist.ps1; evidence-prep=scripts/prepare-yolo-gui-smoke-evidence.ps1; evidence-guide=.tmp/yolo-gui-smoke/gui-smoke-evidence-guide.md; source-invariant=pass; download-button-source-invariant=pass; yolo-numeric-width-source-invariant=pass; preview-playback-queue-source-invariant=pass; preview-cancel-exception-suppressed=pass; lazy-thumbnail-open-source-invariant=pass; open-video-evidence=pass-local-screenshot; select-yolo-backend-evidence=pass-local-screenshot; download-yolo-model-evidence=pass-existing-model-path; run-yolo-auto-detect-evidence=pass-debug-output; preview-result-evidence=pass-local-screenshot; preview-track-hold-evidence=pass-local-recording; manual-edit-evidence=pass-local-screenshot; export-evidence=pass-readable-output; reopen-state-evidence=pass-local-screenshot; startup-smoke-command=pass; startup-smoke-state=pass; manual-evidence-schema=pass; manual-evidence-type-validation=pass; anti-flicker-tracking=pass; manual-verifier-selftest=pass; manual-negative-selftests=pass; gui-checklist-no-clobber=pass; manual-checklist=complete-human-smoke-9-of-9; strict-manual-verifier=pass; required-steps=open-video,select-yolo-backend,download-yolo-model,run-yolo-auto-detect,preview-result,preview-track-hold,manual-edit,export,reopen-state -->
<!-- yolo-track-hold-state: verifier=scripts/verify-yolo-track-hold-state.ps1; MaxLostFillFrames=6; MaxConfirmedTrackHoldFrames=8; AllowSmallTrackLostFill=true; gapFrames=13,14,15,16,17,18,19; lostFilled=6; heldFrames=21,22,23,24,25,26; stop-after-cap=pass; weak-single-frame-candidate=removed; preview-refresh-after-postprocess=pass; tracking-toggle-gated=pass; temporal-smoothing-toggle-gated=pass -->
<!-- yolo-manual-readiness-state: verifier=scripts/verify-yolo-manual-readiness-state.ps1; finalizer=scripts/complete-yolo-goal-after-manual-gates.ps1; finalizer-selftest=pass; full-gt-review-package=ready-pending-human-labels; ai-reviewed-candidate=ready-not-final-gt; ai-candidate-dashboard-reference=reference-only-not-final-gt; human-review-draft=pass; gui-checklist=ready-pending-human-smoke; prepare-gui-checklist=pass; prepare-gui-evidence=pass; manual-gate-next-actions=pass; manual-gate-summary=pass; manual-gate-dashboard=pass; manual-gate-dashboard-progress=pass; manual-gate-open-dashboard=pass; manual-gate-summary-track-hold=pass; manual-pending-report=pass; manual-gate-open-app=pass; manual-gate-final-completion-command=pass; completed-mode=AllowCompletedFullGt+AllowCompletedGuiSmoke; completed-full-gt-reviewed-gate=RequireFullFrameReview+RequireArtifacts+RequireEvidence; ten-minute-artifacts=ready-yolo-output-and-incomplete-faceonnx-baseline -->
<!-- yolo-ready-for-human-gates-state: verifier=scripts/verify-yolo-ready-for-human-gates-state.ps1; top-level=verify-auto-mosaic-default.ps1 -RunYoloReadyForHumanGatesState; top-level-ready-rerun=pass; evidence-report=pass; evidence-report-dynamic=pass; evidence-report-candidate-comparison=pass; evidence-report-full-gt-quality=fail-documented; manual-pending-report=pass; manual-gate-progress=pass; manual-gate-open-dashboard=pass; completion-finalizer=pass; completion-finalizer-state=pass; evidence-report-path=.tmp/yolo-manual-gates/goal-evidence-report.md; yolo-state=pass; manual-gate-summary=pass; completion-audit=pass-incomplete; ready=true; remaining=gui-smoke -->

YOLO 紐⑺몴 ?꾨즺 媛먯궗:

<!-- yolo-goal-audit-state: backend=integrated; default=FaceONNX; recommendation=none; representative=pass; anti-flicker-tracking=pass; track-hold-state=pass; extended=fail; extended-export=fail; sample-gt=pass; full-gt-harness=pass; full-gt-reviewed=pass; full-gt-quality-failure-allowed=pass; license-source=pass; manual-readiness=pass; ten-minute-full=not-required-after-extended-fail; complete=true; remaining=none; completion-audit=pass-complete; completion-audit-prediction-csv-selftest=pass; completion-audit-marker-only-selftest=pass; completion-audit-pending-negative-selftest=pass; top-level-require-complete-negative=pass; completion-finalizer-state=pass; top-level-require-complete=fast-fail-guarded; top-level-ready-rerun=pass; evidence-report=pass; evidence-report-dynamic=pass; evidence-report-full-gt-quality=fail-documented; manual-gate-dashboard-progress=pass; manual-gate-open-dashboard=pass; empty-yolo-model-args=guarded -->

| ?붽뎄?ы빆 | ?꾩옱 利앷굅 | ?먯젙 |
| --- | --- | --- |
| YOLO backend ?듯빀怨?model蹂?profile 遺꾨━ | `YOLO backend ?좏깮`, YOLOv8-Face/YOLO5Face `model profile 遺꾨━ ???, FaceONNX auto-tune 寃쎈줈? YOLO 寃쎈줈 遺꾨━, FaceONNX/SCRFD/YOLO filter profile 遺꾨━源뚯? 援ы쁽?덇퀬, YOLOv8/YOLO5 profile? threshold/NMS/input/tiling肉??꾨땲??downscale/quality/tracking/detectEvery/parallel session???낅┰ ????곸슜?쒕떎. `verify-yolo-profile-state.ps1`媛 source invariant濡?寃?ы븳?? | 異⑹” |
| FaceONNX 湲곕낯媛?蹂댁〈 | ??湲곕낯 detector??`FaceONNX`?닿퀬, YOLO???ъ슜?먭? 吏곸젒 ?좏깮?섍퀬 ?ъ슜??吏???몃? 紐⑤뜽 寃쎈줈, ?ㅼ슫濡쒕뱶?????곗씠??寃쎈줈, ?먮뒗 ?붾（??濡쒖뺄 `Models/Yolo` 湲곕낯 ?뚯씪紐낆쓣 ?ъ슜?섎뒗 backend/profile 寃쎈줈濡??좎??쒕떎. `verify-yolo-conclusion-state.ps1`? `verify-yolo-distribution-state.ps1`媛 ???곹깭瑜?寃?ы븳?? | 異⑹” |
| YOLO 3珥????gate | `verify-yolo-representative-gate.ps1` 湲곗? YOLO5Face `0.12/0.18/0.45`??lost-fill 6?꾨젅???곸슜 ??`baselineFrames=19`, `optimizedFrames=20`, `onlyBaseline=0`, `onlyOptimized=1(frame 9)`, `avgBestIou=0.971`, `minBestIou=0.944`, `SmokeQualityGate passed=True`?? | 異⑹” |
| ?쒕쾲 紐⑥옄?댄겕 ??????몃옒???좎? | YOLO ?꾩슜 track profile? `MaxLostFillFrames=6`, `MaxConfirmedTrackHoldFrames=8`濡? ?뺤젙 track??吏㏐쾶 誘명깘?섎㈃ ?대? gap? 理쒕? 8?꾨젅?꾧퉴吏 蹂닿컙/hold?섍퀬 track 醫낅즺 ?ㅼ뿉???댁쟾 ?대룞?됱쑝濡?理쒕? 6?꾨젅?꾧퉴吏 留덉뒪?щ? ?좎??쒕떎. ???gate??`lostFilled=6`, `lostFrames=6,7,8,9,10,11`??寃?ы븳?? `verify-yolo-track-hold-state.ps1`???⑹꽦 ?뺤젙 track ?대? gap??`gapFrames=13,14,15,16,17,18,19`濡??좎??섍퀬 醫낅즺 ??`heldFrames=21,22,23,24,25,26`源뚯? lost-fill?섎ŉ cap ?댄썑 frame 27?먯꽌??硫덉텛?붿?, `weak-single-frame-candidate=removed`???⑤컻 ?쏀븳 ?ㅽ깘? ?좎??섏? ?딅뒗吏 ?ㅼ젣 interpolator濡?寃利앺븳?? `WorkspaceViewModel`? track postprocess/ROI/smoothing ?댄썑 ?꾩옱 preview frame???ㅼ떆 ?뚮뜑留곹븯怨? 異붿쟻 toggle off ?곹깭?먯꽌??track postprocess? temporal smoothing???곸슜?섏? ?딅뒗?? GUI smoke checklist??`preview-track-hold` row??`.tmp/yolo-gui-smoke/evidence/preview-track-hold.mp4` recording?쇰줈 梨꾩썱?? | 異⑹”: anti-flicker-tracking pass, track-hold-state pass, preview-refresh-after-postprocess pass, tracking-toggle-gated pass, preview-track-hold GUI recording pass |
| 30珥??댁긽 ?뺤옣 gate | `verify-yolo-extended-gate.ps1` 湲곗? 媛숈? profile? 6遺?30珥?30珥?clip?먯꽌 lost-fill 6?꾨젅???곸슜 ?꾩뿉??`baselineFrames=83`, `optimizedFrames=81`, `onlyBaseline=13`, `onlyOptimized=11`, `minBestIou=0.000`, `SmokeQualityGate passed=False`?? | ?ㅽ뙣 |
| 30珥?export smoke | `verify-yolo-extended-export-gate.ps1` 湲곗? export??FaceONNX/YOLO 紐⑤몢 ?꾨즺?섏?留?FaceONNX `directFaceFrames=83`, YOLO `directFaceFrames=81`?닿퀬 ?댄썑 A/B媛 `SmokeQualityGate passed=False`?? | ?ㅽ뙣 |
| 理쒖쥌 異붿쿇 ?꾨낫 | YOLO5Face?????3珥?gate???듦낵?덉?留?9遺?2珥?諛?6遺?30珥??뺤옣 gate?먯꽌 異붿쿇 ?꾨낫濡??밴꺽?섏? 紐삵뻽怨? ?꾩옱 遺꾨쪟?쒕룄 `?꾩껜 異붿쿇 蹂대쪟`?? | recommendation=none |
| label 湲곕컲 GT 寃利?| `verify-yolo-gt-label-state.ps1` 湲곗? ?쒕낯 crop GT??遺꾨쪟???듦낵?덈떎. 9遺?2珥?pass ?쒕낯? YOLO TP 15/FP 0/miss 0?닿퀬, 6遺?30珥?fail ?쒕낯? YOLO TP 1/FP 10/miss 0, FaceONNX FP 14, unclear 1?대떎. ?ㅼ젣 full-GT review CSV? full-frame review CSV??梨꾩썱怨?`verify-yolo-full-gt-reviewed-state.ps1 -RequireFullFrameReview -RequireEvidence -RequireArtifacts -AllowQualityGateFailure` 湲곗? `gtFaces=8`, `predictions=20`, `truePositive=7`, `miss=1`, `falsePositive=13`, `lowIou=1`, `failureAllowed=True`濡??듦낵?덈떎. ??寃곌낵??YOLO 異붿쿇 ?꾨낫 ?놁쓬??臾몄꽌?붾맂 ?ㅽ뙣 利앷굅?? | 異⑹”: sample-gt pass, full-gt-harness pass, full-gt-reviewed pass, full-gt-quality-failure-allowed pass |
| Avalonia GUI smoke | `verify-yolo-gui-smoke-state.ps1 -RequireManualPass` passes with all 9 checklist rows complete. Local ignored evidence covers `open-video`, `select-yolo-backend`, existing model path, YOLO auto-detect debug output, preview-result screenshot, preview-track-hold recording, manual-edit screenshot, readable export output, and reopen-state screenshot. The fixes for queued exact-frame playback, export-cancel resume state, face-rect mask persistence, startup `--no-auto-export`/`--frame`, lazy thumbnail opening, and YOLO numeric width are covered by source invariants and local smoke evidence. | pass |
| 紐⑤뜽 license/諛고룷 ?먮떒 | 2026-05-23 湲곗? `lindevs/yolov8-face` MIT ?쒖떆, YOLOv8 initial weights 諛?Ultralytics AGPL-3.0/Enterprise caveat, Hugging Face `YoloV5Face.onnx` gpl-3.0 ?쒖떆瑜??ы솗?명뻽?? 2026-05-24???ㅼ슫濡쒕뱶 踰꾪듉??URL???ы솗?명뻽?? ?꾩옱 ?쒗뭹 ?뺤콉? YOLO 紐⑤뜽??repo??異붿쟻?섏? ?딄퀬 installer ?꾩닔 ?뚯씪濡?踰덈뱾?섏? ?딆쑝硫? ?ъ슜?먭? 吏곸젒 吏?뺥븯???몃? 紐⑤뜽 寃쎈줈, ???곗씠???ㅼ슫濡쒕뱶 寃쎈줈, ?먮뒗 ?붾（??濡쒖뺄 `Models/Yolo` 寃쎈줈留??덉슜?쒕떎. | 異⑹”: license-source pass, bundle blocked |
| 10遺꾧툒/?꾩껜 ?곸긽 理쒖쥌 寃利?| YOLO optimized ?⑤룆 10遺??먮룞 寃異?export???꾨즺?덇퀬 ?먮룞 寃異?`totalMs=2,536,529`, export `totalMs=1,375,350`, `directFaceFrames=8063`??湲곕줉?덈떎. FaceONNX 10遺?baseline A/B???꾨즺?섏? ?딆븯吏留? ?꾩옱 YOLO profile? 30珥??뺤옣 gate? export A/B媛 ?대? ?ㅽ뙣?덉쑝誘濡?理쒖쥌 異붿쿇 ?꾨낫 ?밴꺽 議곌굔???꾨떖?섏? 紐삵뻽?? ?곕씪????profile?????10遺?FaceONNX full A/B??異붿쿇 ?먮떒???꾪빐 異붽? ?붽뎄?섏? ?딄퀬, ?ㅼ쓬 異붿쿇 ?꾨낫媛 ?뺤옣 gate瑜??듦낵?????ㅼ떆 ?섑뻾?쒕떎. | 蹂대쪟: not-required-after-extended-fail |

The previous YOLO backend-selection goal marker was `complete=true` and `remaining=none`. The follow-up quality pass below is a separate active quality goal and remains pending until the user-reported flicker, scene-transition ghost, and false-positive cases are visually confirmed. YOLO remains `recommendation=none` because the documented full-GT quality gate still fails under strict zero-miss/zero-false-positive limits and is allowed only as failure evidence, not as a promoted model recommendation.

## 2026-05-25 YOLO Follow-Up Quality Pass

User-reported remaining issues:

- Previously detected faces can still blink or disappear on some frames.
- A tracked mask can survive a scene/cut transition and remain in the next scene.
- YOLO still creates false-positive masks on non-face regions.

Scope for this follow-up is limited to the YOLO path. FaceONNX default behavior must remain unchanged.

Implemented guard rails in this follow-up:

- YOLO track postprocess now reports and removes sparse low-confidence temporal false-positive tracks.
- Low-confidence edge tails are trimmed before lost-frame fill, and edge-lost fill is blocked for weak edge candidates.
- Confirmed edge-start YOLO tracks can now backfill a small number of initial frames before the first strong detection. This targets the visible case where a face is already present but YOLO only locks onto it a few frames later, causing an early flicker gap.
- A YOLO-only scene-cut guard checks filled gap/lost candidates, weak direct track transitions, and short weak post-cut carry runs. Runtime logs include `directCandidates`, `postCutCandidates`, `checkedPairs`, `removedFrames`, and the frame-difference threshold.
- The scene-cut guard now scans the adjacent frame pairs inside each candidate gap and records `maxDiff` plus `cutPairs`, instead of relying only on the source/target endpoint difference. This catches a cut that happens inside a filled/tracked gap even when the two endpoint frames are not the strongest difference pair.
- The same scene-cut guard checks reverse initial-fill candidates by scanning the earlier filled frame through the later source frame while removing the actual earlier candidate frame. This prevents the new anti-flicker backfill from crossing a hard cut.
- The weak direct scene-cut guard now also checks a short low-confidence carry tail after the first weak transition. This targets the reported case where a previous track crosses a cut and leaves mosaic remnants for a few frames in the next scene.
- The scene-cut guard now also builds weak post-cut carry candidates when a short non-edge low-confidence run appears around a frame boundary. This includes the case where the weak run continues from the immediately previous frame, so a hard cut inside a continuous weak YOLO carry can still remove the post-cut frames. These candidates are still removed only when the frame-difference check confirms a cut, and persistent weak runs beyond the carry cap are kept for review instead of being treated as automatic false positives.
- Sparse YOLO materialization checks frame signatures between sampled detections and stops carry-over across detected scene cuts.
- YOLO detector candidates now pass an aspect-ratio filter before temporal tracking.
- A YOLO-only final-mask cleanup now removes weak isolated non-edge final masks after ROI refinement. The cleanup is per-face, not just per-frame: a weak box now needs a matching neighboring box by IoU/center/area continuity to survive, so an unrelated weak box can be removed even when another real face track exists in adjacent frames. It also removes very-low-confidence non-edge clusters that last only 1-2 frames (`WeakClusterMaxConfidence=0.38`), tiny weak non-edge clusters up to 3 frames (`TinyClusterMaxAreaRatio=0.0012`), and very small weak-to-medium non-edge clusters up to 2 frames (`TinyShortClusterMaxConfidence=0.62`, `TinyShortClusterMaxAreaRatio=0.0009`) when there is no matching stronger adjacent continuation. Stronger matching weak masks, strong isolated masks, edge partial-face candidates, high-confidence tiny candidates, and tiny runs that connect to a stronger detection are kept for review so the cleanup does not repeat the risky top/small false-negative behavior.
- Detection dumps include confidence, center, area ratio, and aspect ratio so crop/frame labels can be reviewed without treating YOLO or FaceONNX as ground truth.

Current short-sample evidence:

- `.tmp/yolo-quality/yolo-quality-2s-dump.log` contains 60 processed frames, 19 detection rows, `lostFrames=none`, `removedEdgeTail=1`, `directCandidates=2`, `checkedPairs=3->4,8->9,16->17`, and `removedFrames=none`.
- `.tmp/yolo-quality/yolo-quality-review-checklist.md` lists the frame-level low-confidence/small-area/aspect review points.
- `.tmp/yolo-quality/review-package/review-index.html` was generated with 19 detection crop rows and 8 full-frame overlay rows for human `face`/`nonface`/`miss` labeling.
- `scripts/write-yolo-followup-quality-evidence.ps1` regenerates the short-sample evidence bundle from an existing prediction log or a short YOLO smoke run. It also supports `-TrimStart`, `-TrimSeconds`, and `-ClipPath`, so a long source video can be cut into a focused problem-span clip before YOLO evidence is generated. When `-RunSmoke` is used without trimming, the wrapper now checks source duration with FFprobe metadata and blocks sources longer than `MaxSmokeSourceSeconds=30` unless `-AllowLongSmokeSource` is explicitly passed.
- The repo-local `srcTest/260102_jp_10.mp4` is not a 10-second clip; FFprobe reports about `1067.6s` and `31996` video frames. A shorter `.tmp/srcTest-smoke/smoke-3s.mp4` span was checked separately and produced no YOLO detections (`detections=0`), so it is useful as no-false-positive evidence for that sampled span but not as a crop review package.
- `scripts/verify-face-track-scene-cut-guard.ps1` covers hard-cut removal, same-scene preservation, weak direct transition removal, weak post-cut carry-run removal, continuous weak carry after a hard cut, and frame-diff cache behavior.
- The same scene-cut verifier now covers weak direct carry-tail removal (`directCandidates=3`, `directRemoved=3`), medium-confidence direct carry-tail removal (`mediumDirectCandidates=3`, `mediumDirectRemoved=3`), and weak post-cut carry-run removal (`postCutCandidates=11`, `postCutRemoved=4`) so only the first post-cut frame is not treated as the whole ghost-removal proof.
- `.tmp/yolo-followup-current-0900/yolo-followup-quality-evidence.md` was regenerated from `.tmp/srcTest-smoke/smoke-0900-2s.mp4` with YOLO5Face/DirectML. It contains 100 detection rows, `directCandidates=10`, `checked=24`, `removed=0`, `lostFrames=60,61,60,61`, and review artifacts under `.tmp/yolo-followup-current-0900/review-package/`.
- The 09:00 evidence package exposed an artifact issue where detector/track rows can reference frames that the video reports but FFmpeg cannot decode (`nb_frames=73`, `nb_read_frames=61`). `new-yolo-full-gt-review-package.ps1` now writes a placeholder crop and a notes warning instead of failing, so the row remains auditable without pretending the crop is real video evidence.
- After adding initial backfill, the current default evidence was regenerated at `.tmp/yolo-followup-current-0900-default/yolo-followup-quality-evidence.md`. It contains 103 detection rows, `detector=YoloFaceOnnxDetector/GPU:DirectML`, `detectMs=13440`, `totalMs=7372`, `filled=10`, `lostFilled=4`, `initialFilled=3`, `directCandidates=10`, `checked=27`, `maxDiff=0.086`, `cutPairs=none`, `removedFrames=none`, `removedWeakIsolated=0`, `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, and `lowConf=7`; frames `8-10` now have final mask rows in the review package instead of remaining as a short gap.
- A trial YOLO top/small/low-confidence candidate filter (`centerY <= 0.08`, `areaRatio <= 0.006`, `confidence <= 0.65`) was tested but is not enabled in the app path or smoke harness. It reduced detection rows from `100` to `79` on the 09:00 2-second sample, but crop review showed it also removed visible partial/background faces in frames 4-7 and early `pred-1` rows. Keeping that filter enabled would trade false-positive reduction for new flicker/miss risk, so the option remains available for experiments only and the default path keeps temporal consistency filters as the safer guard.
- `scripts/verify-yolo-profile-state.ps1` now also guards the postprocess order that matters for scene-cut ghost masks: YOLO scene-cut removal must run before ROI refine and temporal smoothing, temporal smoothing must skip empty frames instead of materializing new masks, and ROI refine must only replace an existing matching candidate. This prevents a scene-cut-removed track-fill candidate from being recreated by later postprocess steps.
- The follow-up evidence wrapper now passes required full-frame review frames from `lostFrames`, `checkedPairs`, `cutPairs`, and `removedFrames` into the review package generator before applying the max-row limit. The expanded package at `.tmp/yolo-followup-current-0900-expanded/yolo-followup-quality-evidence.md` records required frames `6,7,18,19,22,23,24,25,26,27,28,29,30,31,52,60,61` and generates full-frame rows for the late hold frames too. If a reported frame cannot be decoded, the generator writes a placeholder image and a CSV note instead of failing or silently dropping the review row.
- The follow-up evidence wrapper also promotes final low-confidence mask frames (`conf <= 0.38`) into the required full-frame review list. This keeps likely false-positive candidates from being dropped by `MaxFullFrameRows`; the current 09:00 package now requires frames `4,6,7,17,20,25,32` in addition to the flicker/scene-cut frames.
- Full-frame overlay review for those current 09:00 low-confidence frames did not prove a non-face false positive: frames `4`, `6`, and `7` cover a visible partial background face near the top edge, and frames `17`, `20`, `25`, and `32` include the same background face plus the foreground face. Treat these rows as low-confidence review targets, not automatic false-positive removals.
- `scripts/verify-yolo-full-gt-review-package-state.ps1` includes a required-frame priority self-test: with `MaxFullFrameRows=1`, a candidate on frame `0`, and `RequiredFullFrameNumbers=9`, the generated full-frame review CSV must contain frame `9` rather than the earlier candidate frame. This proves the review package cannot drop flicker/scene-cut evidence frames merely because early candidate frames filled the cap.
- `scripts/new-yolo-detection-overlay-video.ps1` creates a continuous overlay MP4 from `[SmokeDetection]` rows so flicker/ghost/false-positive candidates can be reviewed in motion, not only as still crops. The current default short-sample overlay is `.tmp/yolo-followup-current-0900-default/yolo-detection-overlay.mp4` (`640x360`, 100 detections), and `scripts/verify-yolo-detection-overlay-video.ps1` regenerates and verifies the overlay generator.
- `scripts/write-yolo-mask-continuity-report.ps1` now writes a final-mask continuity report from `[SmokeDetection]` rows. In this smoke harness those rows are the final `FrameMaskProvider` rectangles after tracking, scene-cut guard, and ROI refinement, so the report lists short empty gaps, isolated final-mask frames, and low-confidence final masks as review targets for flicker/ghost/false-positive checks.
- The previous 09:00 short-sample continuity report listed one short final-mask gap (`8-10`) and marked it as `large box jump; review before fill`. The new initial-fill path addresses that specific early-face flicker without loosening general gap bridging: `scripts/verify-face-track-postprocess.ps1` still keeps incompatible large box jumps unfilled (`largeJumpFilled=False`) while separately asserting `initialFilled=3` for a confirmed edge-start track.
- The scene-cut verifier now covers reverse initial-fill removal (`reverseChecked=1`, `reverseRemoved=1`, `reversePairs=1->3`) so initial backfill is not allowed to create pre-cut ghost masks.
- The GUI automatic mosaic path now logs `[FinalMaskSummary]` for YOLO runs after tracking, scene-cut guard, ROI refinement, and temporal smoothing. The log reports final mask frame count, row count, frame range, short gap ranges, large-jump gap ranges, isolated final-mask frames, and low-confidence final masks, so a debug-output-only report can still identify flicker, ghost, and false-positive review targets.
- The final-mask summary now also reports `weakNonEdge`, `tinyWeak`, and `tinyShort` row counts plus their frame lists after cleanup. These are residual review targets, not automatic errors: they show weak non-edge boxes, tiny weak boxes, and tiny weak-to-medium boxes that survived because they touched an edge, connected to stronger evidence, or otherwise failed the conservative removal rules.
- The GUI automatic mosaic path now also logs `[YoloFinalMaskCleanup] removedWeakIsolated=... removedWeakUnsupported=... removedWeakShortClusters=... removedWeakTinyClusters=... removedTinyShortClusters=... removedTinyIsolated=...` when the final cleanup removes weak or very small unsupported non-edge boxes. The smoke harness emits `[SmokeYoloFinalMaskCleanup] ...` with the same reason counts, and the follow-up evidence summary preserves that line.
- Final cleanup now also removes very small, non-edge, weak-to-medium candidates only when they are isolated or form a tiny short cluster with no stronger continuation (`TinyIsolatedMaxConfidence=0.62`, `TinyShortClusterMaxConfidence=0.62`, `TinyShortClusterMaxFrames=2`). This targets one/two-frame tiny non-face boxes without removing high-confidence tiny candidates or temporally supported tiny runs.
- After YOLO final cleanup, the GUI path now fills only short final-mask gaps whose two anchors are both strong and geometrically stable. The GUI and smoke evidence paths allow stable gaps up to 5 frames, while the service default remains more conservative for callers that do not opt in. This targets residual flicker after tracking/cleanup without filling weak-anchor gaps or large-jump gaps; GUI logs `[YoloFinalMaskGapFill]`, and the smoke harness logs `[SmokeYoloFinalMaskGapFill]`.
- Final-mask gap fill now works per face instead of per frame: if one face is already masked on a frame but another stable face track is missing, the missing face can be added without overwriting the existing face. `scripts/verify-yolo-final-mask-cleanup.ps1` verifies this as `mixedFrameGapFilled=1`.
- Each final-gap fill also carries both previous-anchor and next-anchor evidence into the scene-cut guard, so a stable-looking gap fill is removed again if the frame-difference scan finds a cut on either side of the filled frame. GUI logs this as `[YoloFinalMaskGapFillSceneCutGuard]`, the smoke harness logs `[SmokeYoloFinalMaskGapFillSceneCutGuard]`, and the focused verifier records both before-fill and after-fill deterministic branches as `gapCutRemoved=2`.
- The YOLO scene-cut direct carry candidate range was widened again from weak-only/moderate detections to high transition candidates (`direct<=0.90`, post-cut short carry `<=0.78`). The direct transition branch still requires a small confidence drop (`>=0.06`) before a candidate can be checked, and the short carry tail is capped at 5 frames, while continuous weak carry is handled by the post-cut branch. The guard still removes only when a frame-difference scan crosses the cut threshold, so this targets transition ghost masks without changing FaceONNX.
- After the scene-cut guard removes a YOLO carry candidate, it now removes a short matching weak tail after the cut (`<=5` frames, `confidence<=0.78`) while stopping at a strong matching detection. This closes the case where the cut itself was detected but a visually similar weak residue remained for a few frames after the first removed candidate.
- The same scene-cut guard now also compares the carried source frame directly against the target frame with a lower cumulative threshold (`source->target>=0.36`) after the adjacent-pair scan. This targets fade/transition ghost masks where no single adjacent frame pair reaches the hard-cut threshold but the held mask has moved into a different scene. `scripts/verify-face-track-scene-cut-guard.ps1` covers this as `gradualRemoved=1` and `mildGradualRemoved=1`.
- The YOLO app path now caps post-track lost-fill at `MaxLostFillFrames=3` while keeping initial edge-start backfill at `MaxInitialFillFrames=3`. The smoke harness and follow-up evidence wrapper now use the same split defaults, so short evidence logs match the app profile. The scene-cut guard still removes confirmed post-cut carry/tails after hard cuts.
- YOLO scene-cut checks now run after ROI refinement, final-mask cleanup/gap-fill, and temporal smoothing in the GUI path. This makes scene-cut removal the last YOLO tracking cleanup pass, so later postprocess stages cannot leave a transition-carry mask behind.
- After the final YOLO scene-cut check, the GUI path now runs weak final-mask cleanup once more with stable gap-fill disabled. This removes weak unsupported remnants left after a cut removes only part of a carry run, without creating another post-cut filled mask.
- YOLO scene-cut checks in the app and smoke harness now use the same stricter `0.15` threshold for adjacent and direct frame-difference checks. This keeps the evidence path aligned with the GUI path for transition carry cleanup, and specifically catches the short-sample `maxDiff=0.158` carry that the earlier `0.24` threshold left behind.
- Final-mask gap fill now accepts stable medium-confidence anchors (`MinAnchorConfidence=0.55`, `FillConfidenceFloor=0.48`) instead of requiring `0.58/0.50`. It still rejects weak-anchor gaps and large-jump gaps, and still sends fills through the scene-cut guard before smoothing.
- The smoke harness now emits the equivalent `[SmokeFinalMaskSummary]` for YOLO runs, including `largeJumpGaps`/`largeJumpRanges` and residual `lowConfFrames`/`weakNonEdgeFrames`/`tinyWeakFrames`/`tinyShortFrames`, and `scripts/write-yolo-followup-quality-evidence.ps1` records either `[SmokeFinalMaskSummary]` or GUI `[FinalMaskSummary]` in the generated evidence summary. This keeps GUI logs and CLI evidence aligned.
- `scripts/verify-auto-mosaic-default.ps1` includes FaceONNX/default regression, sparse scene-cut, track scene-cut, aspect-ratio, postprocess, quality-checklist, and follow-up evidence wrapper guards.

Current validation after the follow-up changes:

- `git diff --check` passed.
- `dotnet build FaceShield.sln` passed with the existing 7 FFmpeg obsolete warnings in `Services/Video/VideoExportService.cs`.
- `scripts/verify-yolo-profile-state.ps1` passed and now asserts `largeJumpGaps`/`largeJumpRanges` in both GUI and smoke final-mask summaries, plus `MaxInitialFillFrames`, initial-fill scene-cut candidates, initial-fill ROI refinement, and `initialFilled` smoke logging.
- `scripts/verify-yolo-profile-state.ps1` also asserts that the YOLO final-mask cleanup service is wired into both GUI and smoke paths.
- `scripts/verify-yolo-followup-quality-evidence.ps1` passed and now asserts that generated evidence preserves both the final-mask cleanup line and the large-jump final-mask summary, that low-confidence final detections are promoted into full-frame review requirements, and that long untrimmed `-RunSmoke` sources are guarded by the evidence wrapper.
- `scripts/verify-yolo-final-mask-cleanup.ps1` passed directly: weak isolated non-edge masks are removed, weak edge partial-face masks remain, strong isolated masks remain, adjacent matching weak masks above the very-low-confidence cluster cutoff remain, mixed frames keep the strong face, a weak unrelated box is removed even when another face has temporal neighbors in adjacent frames, a two-frame very-low-confidence non-edge cluster is removed, a three-frame tiny weak non-edge cluster is removed only when it does not connect to a stronger adjacent detection, one medium-confidence tiny isolated non-edge candidate is removed while a high-confidence tiny isolated candidate remains for review, and two-frame weak and medium-confidence tiny non-edge clusters are removed. The verifier now checks reason counters too: `removedWeakUnsupported=3`, `removedWeakShortClusters=2`, `removedWeakTinyClusters=3`, `removedTinyShortClusters=4`, and `removedTinyIsolated=1`. It also verifies final-gap scene-cut removal from both anchor directions: `gapCutRemoved=2`, `gapCutAfterRemoved=1`.
- `scripts/verify-face-track-scene-cut-guard.ps1` passed directly after the stricter scene-transition carry change: `directRemoved=3`, `mediumDirectRemoved=3`, `postCutRemoved=4`, `gradualRemoved=1`, and `mildGradualRemoved=1`.
- `scripts/verify-yolo-final-mask-cleanup.ps1` now also verifies the medium-confidence final-gap fill path with `gapFilled=5` and `gapFrames=11,31,32,33,111`, plus the GUI opt-in 5-frame stable gap path with `extendedGapFilled=5` and `extendedGapFrames=301,302,303,304,305`, while still leaving weak-anchor and large-jump gaps unfilled.
- `scripts/verify-yolo-final-mask-cleanup.ps1` now also verifies post-scene cleanup with `postSceneCleanupRemoved=1`: two adjacent moderate weak boxes survive the first cleanup, the scene-cut guard removes one side of the carry, then final cleanup removes the unsupported remnant.
- `scripts/verify-yolo-final-mask-cleanup.ps1` now also verifies mixed-frame gap fill with `mixedFrameGapFilled=1`, proving a missing face can be added even when another face already exists in the same frame.
- `scripts/verify-yolo-followup-quality-evidence.ps1` now asserts that `write-yolo-followup-quality-evidence.ps1 -RunSmoke` resolves and passes a default YOLO model path to the smoke harness, preventing evidence runs from silently falling back to FaceONNX.
- A focused 2-second fastcheck was regenerated without rebuilding the full review package at `.tmp/yolo-followup-current-0900-fastcheck/yolo-followup-quality-evidence.md`. It confirms the wrapper now runs `detector=YoloFaceOnnxDetector/GPU:DirectML` with 103 detection rows, `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `lowConf=7`, and residual `weakNonEdgeFrames=33`. After restoring the small confidence-drop gate on direct scene candidates, the same sample dropped from `directCandidates=51`, `checked=68`, `elapsedMs=35335` to `directCandidates=14`, `checked=31`, `elapsedMs=16503`; no cut was detected in this no-hard-cut span (`cutPairs=none`, `removedFrames=none`).
- A newer focused 2-second fastcheck after aligning the smoke profile to the app path is `.tmp/yolo-followup-current-0900-lost2-fastcheck/yolo-followup-quality-evidence.md`. It stayed on `detector=YoloFaceOnnxDetector/GPU:DirectML`, kept 103 detection rows, and reports `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `lowConf=7`, residual `weakNonEdgeFrames=33`, and scene-cut guard `directCandidates=14`, `checked=30`, `maxDiff=0.158`, `cutPairs=none`, `removedFrames=none`, `threshold=0.240`.
- The latest post-scene-cleanup focused 2-second fastcheck is `.tmp/yolo-followup-current-postscene-fastcheck/yolo-followup-quality-evidence.md`. It stayed on `detector=YoloFaceOnnxDetector/GPU:DirectML`, kept 103 detection rows, and reports `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, `lowConf=7`, residual `weakNonEdgeFrames=33`, scene-cut guard `directCandidates=20`, `postCutCandidates=6`, `checked=42`, `maxDiff=0.158`, `cutPairs=none`, `removedFrames=none`, `threshold=0.240`, plus `[SmokeYoloFinalMaskPostSceneCleanup] ... removedFrames=none` for this no-hard-cut sample.
- The latest scene-threshold focused 2-second fastcheck is `.tmp/yolo-followup-current-scene-threshold-015-fastcheck/yolo-followup-quality-evidence.md`. It stayed on `detector=YoloFaceOnnxDetector/GPU:DirectML`, kept `shortGaps=0`, `largeJumpGaps=0`, `isolated=0`, and changed the same `maxDiff=0.158` evidence into `cutPairs=19->24,19->25`, `removedFrames=24,25`, `threshold=0.150`. This is the concrete regression evidence for the screen-transition blur carry fix; it does not prove every user-reported span is fixed without visual confirmation on that span.
- The latest matching-tail focused 2-second fastcheck is `.tmp/yolo-followup-current-scene-tail-fastcheck/yolo-followup-quality-evidence.md`. It stayed on `detector=YoloFaceOnnxDetector/GPU:DirectML`, kept `shortGaps=0`, `largeJumpGaps=0`, and `isolated=0`, and expanded the same transition cleanup to `removedFrames=24,25,26,27,28,29`; final mask rows dropped from `101` to `97`. This is stronger evidence for the user-reported screen-transition remnant case, but the original user span still needs visual confirmation before calling the goal complete.
- After the matching-tail change, `scripts/verify-auto-mosaic-default.ps1` was rerun on HEAD `59c916d` and passed. This includes the FaceONNX/default regression gate (`SmokeQualityGate passed=True`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`), the YOLO scene-cut/tail verifier (`weakTailRemoved=3`), sparse scene-cut materialization guards, YOLO final-mask cleanup, aspect-ratio filter, follow-up evidence wrapper, overlay-video generation, ROI representative gate, and default autotune/provider short gate. FFmpeg hardware-decode setup warnings appeared during smoke runs and then continued through the fallback path.
- The problem-span wrapper `scripts/run-yolo-problem-span-verification.ps1` was smoke-tested on a 2-second trimmed sample at `.tmp/yolo-problem-span-wrapper-smoke/yolo-followup-quality-evidence.md`. It produced 97 final rows and preserved the expected scene-cut cleanup evidence: `removedFrames=24,25,26,27,28,29`, `shortGaps=0`, `largeJumpGaps=0`, and `isolated=0`.
- The residual frame 33 weak non-edge candidate is documented as a visual review target rather than an automatic false positive: adjacent frames 32/33/34 contain a stable small candidate with confidence `0.349/0.423/0.580`, center around `0.528-0.529,0.076-0.080`, and area ratio `0.005334-0.005894`. Tightening the default cleanup to delete this pattern would reduce one review target but could also create flicker on small or partial faces.
- The scene-cut guard now skips exact duplicate candidates by source frame, target frame, and rounded bounds before frame-difference checks. It does not merge different boxes on the same source/target pair, because those can be distinct false-positive or real-face candidates that need separate removal decisions. The 2-second dedupe fastcheck at `.tmp/yolo-followup-current-0900-dedupe/yolo-followup-quality-evidence.md` stayed on `detector=YoloFaceOnnxDetector/GPU:DirectML`, kept 103 detection rows and `shortGaps=0`, and changed the no-cut sample from `checked=31` to `checked=30`.
- `scripts/verify-yolo-detection-overlay-video.ps1` passed and regenerated `.tmp/yolo-followup-current-0900-expanded/yolo-detection-overlay.mp4` with 100 detection overlays.
- `scripts/verify-auto-mosaic-default.ps1` passed. This includes FaceONNX/default regression (`SmokeQualityGate passed=True`, `baselineFrames=19`, `optimizedFrames=19`, `avgBestIou=1.000`, `minBestIou=1.000`), scene-cut guards, sparse materialization guard, YOLO aspect-ratio filter guard, follow-up evidence generation, and the continuous overlay-video verifier.
- During the smoke runs, FFmpeg printed hardware-decode setup warnings and then continued through the fallback path; the verifier stages still completed successfully.
- The latest 09:00 default evidence was regenerated after initial-fill and low-confidence-review wiring. The smoke summary preserved `[SmokeFaceTrackPost] ... initialFilled=3`, `[SmokeYoloFinalMaskCleanup] removedWeakIsolated=0`, `[SmokeFinalMaskSummary] ... shortGaps=0 ... largeJumpGaps=0 ... isolated=0 ... lowConf=7`, and review artifacts under `.tmp/yolo-followup-current-0900-default/review-package/` (`103` crop rows and `24` full-frame rows). Required full-frame review frames are now `4,6,7,11,17,18,19,20,22,25,26,27,28,29,30,31,32,52,60,61`, so the low-confidence false-positive candidates are explicitly auditable. The continuous overlay artifact is `.tmp/yolo-followup-current-0900-default/yolo-detection-overlay.mp4` with 103 overlays.
- YOLO synthetic track-fill masks now cap generated gap/initial/lost-fill confidence at `0.78` while leaving FaceONNX defaults at `1.0`. This keeps anti-flicker hold boxes visible inside a scene, but lets scene-cut tail cleanup treat them as generated masks instead of high-confidence detector hits.
- `scripts/verify-yolo-track-hold-state.ps1` now covers that cap end-to-end: a high-confidence (`0.96`) confirmed YOLO track creates lost-fill frames `21-26`, the generated boxes are capped to `0.78`, and the deterministic scene-cut guard removes the entire post-cut tail with `sceneCutLostRemoved=6`.
- Current source evidence does not justify enabling the top-small low-confidence candidate filter by default. The option remains opt-in because the existing state verifier explicitly labels it risky, and earlier filter sweeps recorded small-area/threshold defaults causing strict-gate failures or removing plausible small-face candidates. False-positive decisions still require the frame/crop review artifacts rather than treating any detector as ground truth.

Completion state:

- This follow-up is not yet proven complete against the user-reported problem video. The current evidence proves the code paths and synthetic/short-sample gates, but not that the exact reported flicker, scene-transition ghost, and false-positive cases are gone in the user's visual sample.
- Next completion evidence should use a focused short problem-span clip or an existing run log plus visual review of the generated crop/frame package or equivalent screenshots/recording around the reported frames. Do not use the full source video as a test input; `scripts/run-yolo-problem-span-verification.ps1` requires `-TrimStart`/`-TrimSeconds` and caps the problem span at 30 seconds. The focused procedure and pass/fail criteria are in `YOLO_PROBLEM_SPAN_VERIFICATION.md`.

<!-- yolo-followup-quality-state: default=FaceONNX-preserved; followup-scope=YOLO-only; anti-flicker=track-postprocess-hold-and-initial-fill-guarded; scene-cut=filled-initial-and-weak-direct-guarded; sparse-scene-cut=guarded; false-positive=sparse-edge-tail-aspect-low-confidence-review-guarded; short-sample-review-package=ready; followup-evidence-wrapper=pass; problem-video-visual-confirmation=pending; complete=false; remaining=problem-video-visual-confirmation -->
