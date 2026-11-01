package app.lanbridge.phone.qr

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.Image
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Size
import android.view.Surface
import android.view.TextureView
import android.widget.FrameLayout
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import app.lanbridge.protocol.extractLuma
import com.google.zxing.BinaryBitmap
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import java.util.concurrent.atomic.AtomicBoolean

class QrScanActivity : ComponentActivity() {
    private val done = AtomicBoolean(false)
    private val opening = AtomicBoolean(false)
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private var texture: TextureView? = null
    private var failed = false

    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) openIfReady() else fail("没有相机权限。可以改为输入 IP 和 6 位配对码。")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        try {
            val view = TextureView(this)
            texture = view
            view.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                override fun onSurfaceTextureAvailable(surface: SurfaceTexture, width: Int, height: Int) = openIfReady()
                override fun onSurfaceTextureSizeChanged(surface: SurfaceTexture, width: Int, height: Int) = Unit
                override fun onSurfaceTextureDestroyed(surface: SurfaceTexture): Boolean = true
                override fun onSurfaceTextureUpdated(surface: SurfaceTexture) = Unit
            }
            setContentView(view, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            val worker = HandlerThread("lanbridge-qr")
            worker.start()
            thread = worker
            handler = Handler(worker.looper)
        } catch (error: Throwable) {
            fail("无法打开扫码页：" + (error.message ?: "未知错误"))
        }
    }

    override fun onResume() {
        super.onResume()
        if (failed) return
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (granted) openIfReady() else permission.launch(Manifest.permission.CAMERA)
    }

    override fun onPause() {
        closeCamera()
        super.onPause()
    }

    override fun onDestroy() {
        closeCamera()
        try { thread?.quitSafely() } catch (_: Throwable) {}
        super.onDestroy()
    }

    private fun openIfReady() {
        if (failed || done.get() || opening.get()) return
        val view = texture ?: return
        if (!view.isAvailable) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return
        val worker = handler ?: return
        opening.set(true)
        try {
            val manager = getSystemService(CameraManager::class.java)
            val id = pickCamera(manager) ?: run {
                fail("这台手机没有可用的相机。请改为输入 IP 和配对码。")
                return
            }
            val size = chooseSize(manager, id)
            val images = ImageReader.newInstance(size.width, size.height, android.graphics.ImageFormat.YUV_420_888, 2)
            reader = images
            images.setOnImageAvailableListener({ source ->
                val image = try { source.acquireLatestImage() } catch (_: Throwable) { null } ?: return@setOnImageAvailableListener
                try {
                    val text = decode(image)
                    if (!text.isNullOrBlank() && done.compareAndSet(false, true)) {
                        runOnUiThread {
                            setResult(RESULT_OK, Intent().putExtra("payload", text))
                            finish()
                        }
                    }
                } catch (_: Throwable) {
                } finally {
                    try { image.close() } catch (_: Throwable) {}
                }
            }, worker)
            manager.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    camera = device
                    startSession(device, view, images, worker)
                }
                override fun onDisconnected(device: CameraDevice) {
                    try { device.close() } catch (_: Throwable) {}
                    if (camera === device) camera = null
                    opening.set(false)
                }
                override fun onError(device: CameraDevice, error: Int) {
                    try { device.close() } catch (_: Throwable) {}
                    if (camera === device) camera = null
                    fail("相机打开失败（$error）。请改为输入 IP 和配对码。")
                }
            }, worker)
        } catch (error: SecurityException) {
            fail("没有相机权限。可以改为输入 IP 和 6 位配对码。")
        } catch (error: Throwable) {
            fail("扫码没有启动：" + (error.message ?: "未知错误"))
        }
    }

    private fun startSession(device: CameraDevice, view: TextureView, images: ImageReader, worker: Handler) {
        try {
            val textureSurface = Surface(view.surfaceTexture)
            val targets = listOf(textureSurface, images.surface)
            device.createCaptureSession(targets, object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(capture: CameraCaptureSession) {
                    session = capture
                    opening.set(false)
                    try {
                        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                            addTarget(textureSurface)
                            addTarget(images.surface)
                            set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
                        }.build()
                        capture.setRepeatingRequest(request, null, worker)
                    } catch (error: Throwable) {
                        fail("相机预览失败：" + (error.message ?: "未知错误"))
                    }
                }
                override fun onConfigureFailed(capture: CameraCaptureSession) {
                    tryReaderOnly(device, images, worker)
                }
            }, worker)
        } catch (error: Throwable) {
            tryReaderOnly(device, images, worker)
        }
    }

    private fun tryReaderOnly(device: CameraDevice, images: ImageReader, worker: Handler) {
        try {
            device.createCaptureSession(listOf(images.surface), object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(capture: CameraCaptureSession) {
                    session = capture
                    opening.set(false)
                    try {
                        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                            addTarget(images.surface)
                        }.build()
                        capture.setRepeatingRequest(request, null, worker)
                    } catch (error: Throwable) {
                        fail("相机预览失败：" + (error.message ?: "未知错误"))
                    }
                }
                override fun onConfigureFailed(session: CameraCaptureSession) {
                    fail("这台手机的相机不能用来扫码。请改为输入 IP 和配对码。")
                }
            }, worker)
        } catch (error: Throwable) {
            fail("扫码没有启动：" + (error.message ?: "未知错误"))
        }
    }

    private fun pickCamera(manager: CameraManager): String? {
        val ids = try { manager.cameraIdList } catch (_: Throwable) { return null }
        if (ids.isEmpty()) return null
        val back = ids.firstOrNull { id ->
            try {
                val facing = manager.getCameraCharacteristics(id).get(android.hardware.camera2.CameraCharacteristics.LENS_FACING)
                facing == android.hardware.camera2.CameraCharacteristics.LENS_FACING_BACK
            } catch (_: Throwable) {
                false
            }
        }
        return back ?: ids.first()
    }

    private fun chooseSize(manager: CameraManager, id: String): Size {
        return try {
            val map = manager.getCameraCharacteristics(id)
                .get(android.hardware.camera2.CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            val sizes = map?.getOutputSizes(android.graphics.ImageFormat.YUV_420_888).orEmpty()
            sizes.firstOrNull { it.width <= 1280 && it.height <= 720 } ?: sizes.firstOrNull() ?: Size(640, 480)
        } catch (_: Throwable) {
            Size(640, 480)
        }
    }

    private fun decode(image: Image): String? {
        val plane = image.planes.firstOrNull() ?: return null
        val buffer = plane.buffer
        val bytes = ByteArray(buffer.remaining())
        buffer.get(bytes)
        val luma = extractLuma(bytes, image.width, image.height, plane.rowStride, plane.pixelStride) ?: return null
        val source = PlanarYUVLuminanceSource(luma, image.width, image.height, 0, 0, image.width, image.height, false)
        val reader = QRCodeReader()
        try {
            return reader.decode(BinaryBitmap(HybridBinarizer(source))).text
        } catch (_: Throwable) {
            reader.reset()
        }
        return try {
            reader.decode(BinaryBitmap(HybridBinarizer(source.invert()))).text
        } catch (_: Throwable) {
            null
        }
    }

    private fun closeCamera() {
        opening.set(false)
        try { session?.close() } catch (_: Throwable) {}
        session = null
        try { camera?.close() } catch (_: Throwable) {}
        camera = null
        try { reader?.close() } catch (_: Throwable) {}
        reader = null
    }

    private fun fail(message: String) {
        if (!failed && done.compareAndSet(false, true)) {
            failed = true
            runOnUiThread {
                try { Toast.makeText(this, message, Toast.LENGTH_LONG).show() } catch (_: Throwable) {}
                finish()
            }
        }
    }
}
