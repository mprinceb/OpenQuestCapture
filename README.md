# OpenQuestCapture

[![](https://dcbadge.limes.pink/api/server/y5v3BTXAUd)](https://discord.gg/y5v3BTXAUd)

**Capture and store real-world sensor data on Meta Quest 3**


![Demo](docs/demo.gif)

**[Watch the full pipeline in action](https://www.youtube.com/watch?v=brcnfMmwRq8)** - See how data is captured on Quest and reconstructed into a 3D scene. *(Thank you to EggMan28 for the demo!)*

---

## 🙏 Acknowledgement

First I'd like to acknowledge the work of the author of QuestRealityCapture. This project largely is built on top of the Quest sensor data capture platform / library that they built. **[QuestRealityCapture](https://github.com/t-34400/QuestRealityCapture)** by **[t-34400](https://github.com/t-34400)**.

Huge thanks to the original author for their excellent work in making Quest sensor data accessible!

---

## 📖 Overview

`OpenQuestCapture` is a Unity-based data logging tool for Meta Quest 3 focused on long-duration robotics data collection.  
The default capture pipeline now records:

* both compressed passthrough camera videos (`.mp4`, left and right camera streams)
* synchronized pose logs (`hmd_poses.csv`, controller poses)
* synchronized IMU logs (`imu.csv`)
* camera characteristics JSON (one per stream)

Depth capture and dual raw-YUV capture are disabled by default to reduce storage pressure for multi-hour recording shifts.

---

---

## ✅ Features

* Records HMD/controller poses and IMU data (session-based)
* Captures both compressed passthrough camera streams, to `left_camera.mp4` and `right_camera.mp4`
* Logs Camera2 characteristics for each camera stream
* Uses timestamped session directories for long-run collection
* Keeps recording menu export/delete flows for storage management


### Quick Start Guide

1. **Install the app**: Download from [SideQuest](https://sidequestvr.com/app/45514) or sideload the APK file from the Releases section of this repository. [This video](https://www.youtube.com/watch?v=bsC805t63-E) has a good guide on how to set up sideloading.
2. **!!! IMPORTANT !!! Enable permissions**: **When you first launch the app, make sure to check "Enable Headset Cameras" when the permissions are asked for.**
3. **Start recording**: Launch the app and press the Menu button on the left controller to start a capture session.
4. **Stop recording**: To stop, press the left controller's Menu button again.
5. **Move the data from your Quest to your computer**: The data is stored on the Quest's internal storage. You can move it to your computer using a USB cable by connecting the Quest to your computer and using Windows File Explorer. The data is stored in the `/Quest 3/Internal Shared Storage/data/com.samusynth.OpenQuestCapture/files` directory.
Or, you can use press the Y button on the left controller to toggle the Recording Menu. Select "Export Data" to export the data to a zip file in the Quest 3 Download folder which can be uploaded to Google Drive or other cloud storage services.
6. **Post-process on laptop**: Combine `left_camera.mp4` / `right_camera.mp4` + pose/IMU CSV files into your downstream format (for example, MCAP/Foxglove pipelines).

### 📸 How to take a good capture

To ensure stable long-form robotics capture:

1.  **Lighting**: Ensure the environment is well-lit and consistent. Avoid extreme shadows or blinding lights.
2.  **Movement**: Move steadily and avoid rapid head snaps that cause motion blur.
3.  **Session length**: For shift workflows, run longer sessions and export/delete regularly from the recording menu.

---

## 🧾 Data Structure

Each time you start recording, a new folder is created under:

```
/sdcard/Android/data/com.samusynth.OpenQuestCapture/files
```

Example structure:

```
/sdcard/Android/data/com.samusynth.OpenQuestCapture/files
└── YYYYMMDD_hhmmss/
    ├── left_camera.mp4
    ├── left_camera_characteristics.json
    ├── left_camera_metadata.json
    │
    ├── right_camera.mp4
    ├── right_camera_characteristics.json
    ├── right_camera_metadata.json
    │
    ├── hmd_poses.csv
    ├── left_controller_poses.csv
    ├── right_controller_poses.csv
    └── imu.csv
```

---

## 📄 Data Format Details

### Pose CSV

* Files: `hmd_poses.csv`, `left_controller_poses.csv`, `right_controller_poses.csv`
* Format:

  ```
  unix_time,ovr_timestamp,mono_time_ns,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w
  ```

### Camera Characteristics (JSON)

* Obtained via Android Camera2 API
* Includes pose, intrinsics (fx, fy, cx, cy), sensor info, etc.

### Camera Video (MP4)

* Files: `left_camera.mp4` (left camera), `right_camera.mp4` (right camera)
* Codec: H.264 inside MP4 container
* Audio is recorded on the left stream only — Android allows one microphone capture per process
* Intended for long-duration collection where storage efficiency is critical

Each stream writes its own sidecar metadata — `left_camera_metadata.json` for the left stream,
`right_camera_metadata.json` for the right — holding that stream's start/stop stamps:

```json
{
  "recording_start_unix_ms": 1753267694123,
  "recording_stop_unix_ms": 1753267754456,
  "recording_start_mono_ns": 84213000000,
  "recording_stop_mono_ns": 144546000000,
  "configured_fps": 30,
  "video_file": "right_camera.mp4",
  "audio_enabled": false,
  "audio_bitrate": 128000,
  "audio_sampling_rate": 44100
}
```

`recording_start_mono_ns` is on the same monotonic clock as the CSVs' `mono_time_ns` column
and the `POST /api/timesync/ping` endpoint, so the two videos and every logged sample map
onto a host clock with `host_time = mono_time_ns + measured_offset`.

---

## 🚀 Installation & Usage

### For End Users

**Option 1: Install via SideQuest**

Download and install directly from [SideQuest](https://sidequestvr.com/app/45514).

**Option 2: Manual Sideloading**

Side-loading is required to install this app on the Quest. [This video](https://www.youtube.com/watch?v=bsC805t63-E) has a good guide on how to set up sideloading. The most up to date APK can be found in the [Releases](https://github.com/samuelm2/OpenQuestCapture/releases) section of this repository.


## 🎮 Usage

### Recording & Management

1. **Start/Stop Recording**: 
   - Launch the app.
   - Press the **Menu button** on the left controller to dismiss the instruction panel and start logging.
   - To stop, simply close the app or pause the session.

2. **Manage Recordings**:
   - Press the **Y button** on the left controller to toggle the **Recording Menu**.
   - This menu allows you to:
     - **View** a list of all recorded sessions.
     - **Export** sessions to a zip file (saved directly to the Quest Downloads folder: `/Download/Export/`).
     - **Delete** unwanted sessions to free up space.


---

## ☁️ Cloud Processing (Recommended)

For the easiest workflow, you can upload your exported `.zip` files directly to the vid2scene cloud processing service:

**[vid2scene.com/upload/quest](https://vid2scene.com/upload/quest)**

This service will automatically process your data and generate a 3D reconstruction.

---

## 💻 Local Processing & Reconstruction

If you prefer to process data locally, this project includes a submodule **[quest-3d-reconstruction](https://github.com/samuelm2/quest-3d-reconstruction)** with powerful Python scripts.

### Setup

Ensure you have the submodule initialized:

```bash
git submodule update --init --recursive
```

### End-to-End Pipeline

The `e2e_quest_to_colmap.py` script provides a one-step solution to convert your Quest data into a COLMAP format.

**Usage Example:**

```bash
python quest-3d-reconstruction/scripts/e2e_quest_to_colmap.py \
  --project_dir /path/to/extracted/session/folder \
  --output_dir /path/to/output/colmap/project \ 
  --use_colored_pointcloud
```

Once in colmap format, the reconstruction can be passed into various Gaussian Splatting tools to generate a Gaussian Splatting scene.

**What this script does:**
1. **Converts YUV images** to RGB.
2. **Reconstructs the 3D scene** (point cloud).
3. **Exports** everything (images, cameras, points) to a COLMAP sparse model.

---

## 🎨 Tone Mapping for Indoor Scenes

If you're capturing indoor scenes with windows, you may notice that bright window light can blow out the interior lighting, resulting in overexposed highlights and underexposed shadows. The reconstruction pipeline includes **tone mapping** (CLAHE + gamma correction) to fix this issue during YUV→RGB conversion.

This is **especially effective for indoor environments** where natural light from windows creates high dynamic range conditions that exceed the camera's capture capability.

### How to Enable

Edit `quest-3d-reconstruction/config/pipeline_config.yml` before running the reconstruction:

```yaml
yuv_to_rgb:
  tone_mapping: true              # Enable tone mapping
  tone_mapping_method: "clahe+gamma"  # Options: "clahe", "gamma", "clahe+gamma"
  clahe_clip_limit: 2.0           # Contrast enhancement (1.0-4.0)
  clahe_tile_grid_size: 8         # Local adaptation grid size (4-16)
  gamma_correction: 1.2           # Brightness boost (>1 brightens)
```

Then run the pipeline as normal. The tone mapping will be applied automatically when converting YUV images to RGB.

For more details and advanced options, see the full documentation in the [quest-3d-reconstruction README](https://github.com/samuelm2/quest-3d-reconstruction#-tone-mapping-for-high-dynamic-range-scenes).

---

## 🛠 Environment

* Unity **6000.2.9f1**
* Meta OpenXR SDK
* Meta MRUK (Mixed Reality Utility Kit)
* Device: Meta Quest 3 or 3s only


---

## 📝 License

This project is licensed under the **[MIT License](LICENSE)**.

This project uses Meta’s OpenXR SDK — please ensure compliance with its license when redistributing.

---

## 📌 Call for Contributions

This project is in its early stages and is still in active development. If you have any suggestions, bug reports, or feature requests, please open an issue or submit a pull request.

Join our Discord community to discuss ideas, get help, and share your captures with other users:

[![](https://dcbadge.limes.pink/api/server/y5v3BTXAUd)](https://discord.gg/y5v3BTXAUd)

One area improvment is the export process. Currently it can take several minutes to export a session. It would be great to have a faster way to do this.
