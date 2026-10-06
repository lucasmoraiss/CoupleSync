// AC-002 / AC-003: React Native module bridge that forwards notification events
// from NotificationCaptureService to the JS layer via DeviceEventEmitter.
package com.couplesync.app

import android.content.ComponentName
import android.content.Intent
import android.provider.Settings
import com.facebook.react.bridge.LifecycleEventListener
import com.facebook.react.bridge.Promise
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod
import com.facebook.react.bridge.WritableNativeMap
import com.facebook.react.modules.core.DeviceEventManagerModule

class NotificationBridgeModule(private val reactContext: ReactApplicationContext) :
    ReactContextBaseJavaModule(reactContext) {

    override fun getName(): String = "NotificationBridge"

    init {
        // Register with the static event bus so the service can forward events
        NotificationEventBus.listener = { packageName, title, body, timestampMs ->
            if (reactContext.hasActiveReactInstance()) {
                val params = WritableNativeMap().apply {
                    putString("packageName", packageName)
                    putString("title", title)
                    putString("body", body)
                    putDouble("timestampMs", timestampMs.toDouble())
                }
                reactContext
                    .getJSModule(DeviceEventManagerModule.RCTDeviceEventEmitter::class.java)
                    .emit("NotificationCaptured", params)
            } else {
                // Bridge registered but React instance not yet active — re-buffer (FR-005).
                // Direto no buffer: chamar dispatch aqui reentraria neste mesmo listener, em recursão infinita (MOB-08).
                NotificationEventBus.buffer(packageName, title, body, timestampMs)
            }
        }

        // Flush events buffered while the bridge was inactive (NFR-001: no title/body logged).
        flushBuffered()

        // Um evento re-bufferizado porque o React ainda não estava ativo só sairia na próxima criação do módulo.
        // Ao voltar ao primeiro plano (React ativo) o que estiver em espera é entregue.
        reactContext.addLifecycleEventListener(object : LifecycleEventListener {
            override fun onHostResume() = flushBuffered()
            override fun onHostPause() {}
            override fun onHostDestroy() {}
        })
    }

    /**
     * Entrega ao JS o que estava em espera; se o React continua inativo, o evento volta ao buffer
     * (limite de 50 mantido pelo próprio buffer; direto no buffer, sem passar pelo listener: sem recursão).
     */
    private fun flushBuffered() {
        NotificationEventBus.flush { packageName, title, body, timestampMs ->
            if (reactContext.hasActiveReactInstance()) {
                val params = WritableNativeMap().apply {
                    putString("packageName", packageName)
                    putString("title", title)
                    putString("body", body)
                    putDouble("timestampMs", timestampMs.toDouble())
                }
                reactContext
                    .getJSModule(DeviceEventManagerModule.RCTDeviceEventEmitter::class.java)
                    .emit("NotificationCaptured", params)
            } else {
                NotificationEventBus.buffer(packageName, title, body, timestampMs)
            }
        }
    }

    /**
     * Check whether this app has been granted the Notification Listener permission.
     * Uses the same method Android uses internally to verify listener access.
     */
    @ReactMethod
    fun isPermissionGranted(promise: Promise) {
        try {
            val flat = Settings.Secure.getString(
                reactContext.contentResolver,
                "enabled_notification_listeners",
            ) ?: ""
            val componentName = ComponentName(reactContext, NotificationCaptureService::class.java)
            promise.resolve(flat.contains(componentName.flattenToString()))
        } catch (e: Exception) {
            promise.resolve(false)
        }
    }

    /**
     * Liga/desliga a captura no serviço nativo (consentimento do usuário). Desligada, o serviço ignora
     * as notificações e descarta o que estava em espera. O app chama isto a cada mudança de consentimento.
     */
    @ReactMethod
    fun setCaptureEnabled(enabled: Boolean) {
        NotificationEventBus.setCaptureEnabled(reactContext, enabled)
        // O JS acabou de ligar a captura (e já escuta): entrega o que ficou em espera.
        if (enabled) flushBuffered()
    }

    /**
     * Open the system Notification Access settings screen so the user can
     * grant or revoke the permission. No credentials are requested or stored.
     */
    @ReactMethod
    fun openNotificationListenerSettings() {
        val intent = Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK
        }
        reactContext.startActivity(intent)
    }

    /**
     * Called by React Native when the bridge is torn down (e.g. hot reload).
     * Nulls the static listener to prevent holding a stale ReactApplicationContext.
     */
    override fun invalidate() {
        NotificationEventBus.listener = null
    }
}
