package app.jarvis.companion

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Before
import org.junit.Test
import java.net.InetAddress
import java.security.KeyStore
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import kotlin.concurrent.thread

/**
 * Certificate pinning against a real HTTPS server on this machine. The keystores in src/test/resources are
 * test-only self-signed certificates (like the one JARVIS makes for itself), never used anywhere else.
 */
class PinningTest {
    // A minimal HTTPS server on plain javax.net.ssl (part of Android's API, so it compiles for unit tests too).
    private lateinit var server: SSLServerSocket
    private lateinit var base: String
    private var port = 0

    private fun keystore(name: String): KeyStore =
        KeyStore.getInstance("PKCS12").apply { PinningTest::class.java.classLoader!!.getResourceAsStream(name).use { load(it, "changeit".toCharArray()) } }

    private fun fingerprint(name: String): String {
        val ks = keystore(name)
        return JarvisClient.fingerprintOf(ks.getCertificate(ks.aliases().nextElement()) as X509Certificate)
    }

    @Before
    fun start() {
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(keystore("jarvis-test.p12"), "changeit".toCharArray()) }
        val ssl = SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, null) }
        server = ssl.serverSocketFactory.createServerSocket(0, 10, InetAddress.getByName("127.0.0.1")) as SSLServerSocket
        port = server.localPort
        base = "https://127.0.0.1:$port"
        thread(isDaemon = true) {
            while (!server.isClosed) {
                val socket = try { server.accept() } catch (e: Exception) { break }
                thread(isDaemon = true) {
                    try {
                        socket.use { s ->
                            val reader = s.getInputStream().bufferedReader()
                            var auth: String? = null
                            while (true) {
                                val line = reader.readLine() ?: break
                                if (line.isEmpty()) break
                                if (line.startsWith("Authorization:", ignoreCase = true)) auth = line.substringAfter(":").trim()
                            }
                            val ok = auth == "Bearer secret"
                            val body = if (ok) "{\"name\":\"JARVIS\"}" else "{\"error\":\"not paired\"}"
                            val bytes = body.toByteArray()
                            val head = "HTTP/1.1 ${if (ok) "200 OK" else "401 Unauthorized"}\r\nContent-Type: application/json\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n"
                            s.getOutputStream().apply { write(head.toByteArray()); write(bytes); flush() }
                        }
                    } catch (e: Exception) {
                        // A client that refused our certificate drops the handshake: nothing to answer.
                    }
                }
            }
        }
    }

    @After
    fun stop() = server.close()

    @Test
    fun talks_to_the_paired_jarvis() {
        val reply = JarvisClient(base, fingerprint("jarvis-test.p12"), "secret").get("/companion/api/status")
        assertEquals(200, reply.code)
        assertEquals("{\"name\":\"JARVIS\"}", reply.body)
    }

    @Test
    fun sends_the_device_token_only_to_the_pinned_server() {
        // A server presenting any other certificate is refused before a single byte (or the token) is sent.
        assertThrows(WrongCertificateException::class.java) {
            JarvisClient(base, fingerprint("other-test.p12"), "secret").get("/companion/api/status")
        }
    }

    @Test
    fun a_missing_or_wrong_token_is_reported_by_status() {
        assertEquals(401, JarvisClient(base, fingerprint("jarvis-test.p12"), "wrong").get("/companion/api/status").code)
    }

    @Test
    fun plain_http_is_never_used() {
        assertThrows(CertificateException::class.java) {
            JarvisClient("http://127.0.0.1:$port", fingerprint("jarvis-test.p12"), "secret").get("/companion/api/status")
        }
    }
}
