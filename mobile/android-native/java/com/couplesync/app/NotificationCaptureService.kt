// AC-002: Android NotificationListenerService that receives bank push notifications
// and forwards them to the React Native bridge for parsing and upload.
// Package must match app.json android.package = com.couplesync.app
package com.couplesync.app

import android.app.Notification
import android.content.Context
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

/**
 * Thread-safe static event bus shared between [NotificationCaptureService]
 * and [NotificationBridgeModule]. Includes a bounded FIFO buffer (max 50 events)
 * so notifications arriving before the bridge initialises are not dropped.
 *
 * NOTE: This is an in-process singleton; destroyed with the process.
 * Sufficient for V1 pilot scale.
 */
object NotificationEventBus {
    private const val MAX_BUFFER_SIZE = 50
    private const val PREFS_NAME = "couplesync_capture"
    private const val KEY_ENABLED = "enabled"

    @Volatile
    var listener: ((packageName: String, title: String, body: String, timestampMs: Long) -> Unit)? = null

    private val buffer = mutableListOf<NotificationEvent>()
    private val lock = Any()

    // Guarda explícita contra recursão: se um listener (direta ou indiretamente) chamar dispatch de novo
    // na mesma thread, o evento vai para o buffer em vez de reentrar no listener (MOB-08).
    private val dispatching = ThreadLocal<Boolean>()

    // Consentimento do usuário (MOB-03): a captura só roda depois de o app (JS) liberar, e o valor fica salvo
    // para o serviço, que o sistema pode iniciar sem o app aberto. Padrão: desligada.
    @Volatile
    private var captureEnabled: Boolean? = null

    data class NotificationEvent(
        val packageName: String,
        val title: String,
        val body: String,
        val timestampMs: Long
    )

    fun isCaptureEnabled(context: Context): Boolean {
        captureEnabled?.let { return it }
        val stored = context.applicationContext
            .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getBoolean(KEY_ENABLED, false)
        captureEnabled = stored
        return stored
    }

    fun setCaptureEnabled(context: Context, enabled: Boolean) {
        context.applicationContext
            .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_ENABLED, enabled).apply()
        captureEnabled = enabled
        if (!enabled) {
            // Desligar descarta o que ainda esperava a ponte: nada capturado antes do "desligar" sai depois dele.
            synchronized(lock) { buffer.clear() }
        }
    }

    /** Guarda o evento sem acionar o listener. Usado quando o listener existe mas ainda não pode entregar. */
    fun buffer(packageName: String, title: String, body: String, timestampMs: Long) {
        synchronized(lock) {
            if (buffer.size >= MAX_BUFFER_SIZE) {
                buffer.removeAt(0) // Drop oldest
            }
            buffer.add(NotificationEvent(packageName, title, body, timestampMs))
        }
    }

    fun dispatch(packageName: String, title: String, body: String, timestampMs: Long) {
        val currentListener = listener
        if (currentListener == null || dispatching.get() == true) {
            buffer(packageName, title, body, timestampMs)
            return
        }
        dispatching.set(true)
        try {
            currentListener(packageName, title, body, timestampMs)
        } finally {
            dispatching.set(false)
        }
    }

    fun flush(sink: (packageName: String, title: String, body: String, timestampMs: Long) -> Unit) {
        val pending: List<NotificationEvent>
        synchronized(lock) {
            pending = buffer.toList()
            buffer.clear()
        }
        for (event in pending) {
            sink(event.packageName, event.title, event.body, event.timestampMs)
        }
    }
}

/**
 * Supported bank package names.  Keep in sync with notification-patterns.json.
 * Early filtering here avoids unnecessary IPC to the JS layer.
 */
private val SUPPORTED_PACKAGES = setOf(
    "com.nu.production",
    "com.itau",
    "br.com.italiquido",
    "com.itau.empresas",
    "br.com.intermedium",
    "com.c6bank.app",
    "com.bradesco",
    "com.bradesco.prestoandroid",
    "com.bradesco.next",
)

class NotificationCaptureService : NotificationListenerService() {

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        sbn ?: return

        // Sem consentimento (ou com a captura desligada) a notificação nem é lida (MOB-03).
        if (!NotificationEventBus.isCaptureEnabled(applicationContext)) return

        val packageName = sbn.packageName ?: return
        if (packageName !in SUPPORTED_PACKAGES) return

        val extras: Bundle = sbn.notification?.extras ?: return

        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim() ?: ""
        // Prefer big text (expanded) which carries the full transaction detail
        val body = (extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: extras.getCharSequence(Notification.EXTRA_TEXT))
            ?.toString()?.trim() ?: ""

        if (body.isEmpty()) return

        // Dispatch to bridge module via the static event bus
        NotificationEventBus.dispatch(packageName, title, body, sbn.postTime)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        // Not used in V1
    }
}
