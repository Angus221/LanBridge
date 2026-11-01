package app.lanbridge.phone.net

import android.content.Context
import android.net.wifi.WifiManager
import app.lanbridge.phone.session.DiscoveredPc
import app.lanbridge.protocol.MdnsBrowse
import java.net.DatagramPacket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.MulticastSocket
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean

class LanMdnsBrowser(
    private val context: Context,
    private val onFound: (DiscoveredPc) -> Unit,
    private val onStatus: (String) -> Unit
) {
    private val running = AtomicBoolean(false)
    private var thread: Thread? = null
    private var socket: MulticastSocket? = null

    fun start() {
        stop()
        running.set(true)
        thread = Thread({
            try {
                loop()
            } catch (ex: Exception) {
                if (running.get()) onStatus("组播搜索中断：" + (ex.message ?: "网络错误"))
            }
        }, "lanbridge-mdns").also { it.isDaemon = true; it.start() }
    }

    fun stop() {
        running.set(false)
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        val current = thread
        thread = null
        if (current != null && current != Thread.currentThread()) {
            try { current.join(400) } catch (_: Exception) {}
        }
    }

    private fun loop() {
        val opened = MulticastSocket(null)
        opened.reuseAddress = true
        opened.timeToLive = 255
        opened.soTimeout = 1000
        val wifi = wifiIpv4()
        val nic = wifi?.let { address ->
            try { NetworkInterface.getByInetAddress(address) } catch (_: Exception) { null }
        }
        if (nic != null) {
            try { opened.networkInterface = nic } catch (_: Exception) {}
        }
        if (wifi != null) opened.bind(InetSocketAddress(wifi, 0)) else opened.bind(InetSocketAddress(0))
        socket = opened
        val group = InetAddress.getByName("224.0.0.251")
        if (nic != null) {
            try { opened.joinGroup(InetSocketAddress(group, 5353), nic) } catch (_: Exception) {}
        }
        val query = MdnsBrowse.query()
        var sentAt = 0L
        val buffer = ByteArray(4096)
        while (running.get()) {
            val now = System.currentTimeMillis()
            if (now - sentAt > 3000) {
                sentAt = now
                try {
                    opened.send(DatagramPacket(query, query.size, group, 5353))
                } catch (ex: Exception) {
                    if (running.get()) onStatus("发出发现请求失败：" + (ex.message ?: "网络错误"))
                }
            }
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                opened.receive(packet)
            } catch (_: SocketTimeoutException) {
                continue
            } catch (_: Exception) {
                if (!running.get()) break
                continue
            }
            val found = try {
                MdnsBrowse.collect(buffer.copyOf(packet.length))
            } catch (_: Exception) {
                null
            } ?: continue
            val name = found.txt["name"].orEmpty().ifBlank { found.instance.substringBefore('.') }
            onFound(
                DiscoveredPc(
                    name = name,
                    host = found.host,
                    port = found.port,
                    deviceId = found.txt["id"].orEmpty(),
                    fingerprint = found.txt["fp"].orEmpty().lowercase()
                )
            )
        }
        try { opened.close() } catch (_: Exception) {}
    }

    private fun wifiIpv4(): InetAddress? {
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return null
        @Suppress("DEPRECATION")
        val bits = wifi.connectionInfo?.ipAddress ?: return null
        if (bits == 0) return null
        val bytes = byteArrayOf(
            (bits and 0xff).toByte(),
            (bits shr 8 and 0xff).toByte(),
            (bits shr 16 and 0xff).toByte(),
            (bits shr 24 and 0xff).toByte()
        )
        return InetAddress.getByAddress(bytes)
    }
}
