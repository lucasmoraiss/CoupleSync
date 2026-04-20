// AC-002: Android NotificationListenerService that receives bank push notifications
// and forwards them to the React Native bridge for parsing and upload.
// Package must match app.json android.package = com.couplesync.app
package com.couplesync.app

import android.app.Notification
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

/**
 * Thread-safe static event bus shared between [NotificationCaptureService]
 * and [NotificationBridgeModule]. Uses @Volatile for single-field visibility.
 *
 * NOTE: This is an in-process singleton; destroyed with the process.
 * Sufficient for V1 pilot scale.
 */
object NotificationEventBus {
    @Volatile
    var listener: ((packageName: String, title: String, body: String, timestampMs: Long) -> Unit)? =
        null
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
        NotificationEventBus.listener?.invoke(packageName, title, body, sbn.postTime)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        // Not used in V1
    }
}
