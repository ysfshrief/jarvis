package app.jarvis.companion

import com.sun.net.httpserver.HttpsConfigurator
import com.sun.net.httpserver.HttpsServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Before
import org.junit.Test
import java.net.InetSocketAddress
import java.security.KeyStore
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext

/**
 * Certificate pinning against a real HTTPS server on this machine. The keystores in src/test/resources are
 * test-only self-signed certificates (like the one JARVIS makes for itself), never used anywhere else.
 */
class PinningTest {
    private lateinit var server: HttpsServer
    private lateinit var base: String

    private fun keystore(name: String): KeyStore =
        KeyStore.getInstance("PKCS12").apply { javaClass.classLoader!!.getResourceAsStream(name).use { load(it, "changeit".toCharArray()) } }

    private fun fingerprint(name: String): String {
        val ks = keystore(name)
        return JarvisClient.fingerprintOf(ks.getCertificate(ks.aliases().nextElement()) as X509Certificate)
    }

    @Before
    fun start() {
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(keystore("jarvis-test.p12"), "changeit".toCharArray()) }
        val ssl = SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, null) }
        server = HttpsServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.httpsConfigurator = HttpsConfigurator(ssl)
        server.createContext("/companion/api/status") { ex ->
            val auth = ex.requestHeaders.getFirst("Authorization")
            val body = (if (auth == "Bearer secret") "{\"name\":\"JARVIS\"}" else "{\"error\":\"not paired\"}").toByteArray()
            ex.sendResponseHeaders(if (auth == "Bearer secret") 200 else 401, body.size.toLong())
            ex.responseBody.use { it.write(body) }
        }
        server.start()
        base = "https://127.0.0.1:${server.address.port}"
    }

    @After
    fun stop() = server.stop(0)

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
            JarvisClient("http://127.0.0.1:${server.address.port}", fingerprint("jarvis-test.p12"), "secret").get("/companion/api/status")
        }
    }
}
