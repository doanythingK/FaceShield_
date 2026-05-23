# YOLO model files

Place optional local YOLO ONNX model files in this folder when you want the app
to auto-detect them without choosing an external path in the UI. The app's
download button stores models in the user's local application data folder
instead of this repository folder, so downloaded weights are not committed.

Supported default file names:

- `YoloV5Face.onnx`
- `Yolo5Face.onnx`
- `yolov8n-face-lindevs.onnx`
- `yolov8s-face-lindevs.onnx`
- `yolov8m-face-lindevs.onnx`
- `yolov8l-face-lindevs.onnx`

The app still defaults to FaceONNX. YOLO model files are ignored by git in this
folder because their license and distribution terms must be cleared separately.
