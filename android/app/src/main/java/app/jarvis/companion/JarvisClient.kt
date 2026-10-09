package app.jarvis.companion

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/** The certificate presented isn't the one this phone paired with. */
class WrongCertificateException : java.io.IOException("This isn't the JARVIS you paired with (its certificate is different).")

data class Reply(val code: Int, val body: String) {
    val ok get() = code in 200..299
    fun json(): JSONObject = if (body.isBlank()) JSONObject() else JSONObject(body)
    fun error(): String = runCatching { json().optString("error") }.getOrNull().orEmpty().ifBlank { "JARVIS answered $code." }
}

/**
 * HTTPS to JARVIS on the local network, trusting exactly one certificate: the one whose SHA-256 fingerprint
 * the PC showed at pairing time. Public CAs are deliberately not trusted for this connection, and the
 * address isn't used for identity (it changes with DHCP) — the pinned certificate is.
 */
class JarvisClient(baseUrl: String, private val fingerprint: String, private val token: String?) {
    private val base = baseUrl.trimEnd('/')

    companion object {
        fun fingerprintOf(cert: X509Certificate): String =
            MessageDigest.getInstance("SHA-256").digest(cert.encoded).joinToString(":") { "%02X".format(it) }

        /** "ab cd:ef…" → "AB:CD:EF…" */
        fun normalize(fp: String): String = fp.uppercase().filter { it in "0123456789ABCDEF" }.chunked(2).joinToString(":")

        /** Reads jarvis://pair?u=…&c=…&fp=… */
        fun parsePairLink(text: String): Triple<String, String, String>? {
            val uri = runCatching { java.net.URI(text.trim()) }.getOrNull() ?: return null
            if (uri.scheme != "jarvis" || uri.host != "pair") return null
            val q = (uri.rawQuery ?: return null).split('&').mapNotNull {
                val i = it.indexOf('=')
                if (i <= 0) null else it.substring(0, i) to java.net.URLDecoder.decode(it.substring(i + 1), "UTF-8")
            }.toMap()
            val u = q["u"]?.trimEnd('/') ?: return null
            val c = q["c"] ?: return null
            val fp = normalize(q["fp"] ?: return null)
            if (!u.startsWith("https://") || fp.length != 95) return null
            return Triple(u, c, fp)
        }
    }

    @Volatile private var pinMismatch = false

    private val trust = object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
            throw CertificateException("Client certificates aren't used.")

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val leaf = chain?.firstOrNull()
            if (leaf == null || fingerprintOf(leaf) != fingerprint) {
                pinMismatch = true
                throw CertificateException("Certificate fingerprint doesn't match the pairing.")
            }
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    private val sockets: SSLSocketFactory by lazy {
        SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }.socketFactory
    }

    // Identity comes from the pinned certificate above, not from the (changing) LAN address.
    private val anyHost = HostnameVerifier { _, _ -> true }

    fun get(path: String): Reply = send("GET", path, null)
    fun post(path: String, body: JSONObject): Reply = send("POST", path, body)

    private fun send(method: String, path: String, body: JSONObject?): Reply {
        val conn = URL(base + path).openConnection() as HttpURLConnection
        if (conn !is HttpsURLConnection) throw CertificateException("JARVIS is only reached over HTTPS.")
        conn.sslSocketFactory = sockets
        conn.hostnameVerifier = anyHost
        conn.requestMethod = method
        conn.connectTimeout = 6_000
        conn.readTimeout = if (path.endsWith("/chat")) 120_000 else 15_000
        conn.setRequestProperty("Accept", "application/json")
        token?.let { conn.setRequestProperty("Authorization", "Bearer $it") }
        try {
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            val stream = if (code < 400) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            return Reply(code, text)
        } catch (e: java.io.IOException) {
            // Android reports a refused certificate as a generic handshake failure; say what actually happened.
            if (pinMismatch) throw WrongCertificateException()
            throw e
        } finally {
            conn.disconnect()
        }
    }
}
