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

    // O JS avisou que já escuta os eventos (setCaptureEnabled(true), chamado DEPOIS de registrar o ouvinte).
    // Enquanto for false nada é emitido: o módulo é criado junto com o React, antes de qualquer tela, e um evento
    // emitido nesse intervalo (ou com a área principal do app desmontada) não chegaria a ninguém.
    @Volatile
    private var deliverToJs = false

    // Ao voltar ao primeiro plano, o que ficou em espera é entregue, se o JS estiver escutando.
    private val lifecycleListener = object : LifecycleEventListener {
        override fun onHostResume() {
            if (deliverToJs) flushBuffered()
        }

        override fun onHostPause() {}

        override fun onHostDestroy() {}
    }

    init {
        // Register with the static event bus so the service can forward events
        NotificationEventBus.listener = { packageName, title, body, timestampMs ->
            deliverOrBuffer(packageName, title, body, timestampMs)
        }
        // Nothing is flushed here: events buffered while the app was closed wait until the JS side is listening.
        reactContext.addLifecycleEventListener(lifecycleListener)
    }

    /**
     * Entrega o evento ao JS, ou o devolve ao buffer quando o JS ainda não escuta ou o React não está ativo
     * (limite de 50 mantido pelo próprio buffer). Direto no buffer, nunca por dispatch: sem recursão (MOB-08).
     * NFR-001: título e corpo nunca são registrados em log.
     */
    private fun deliverOrBuffer(packageName: String, title: String, body: String, timestampMs: Long) {
        if (deliverToJs && reactContext.hasActiveReactInstance()) {
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

    private fun flushBuffered() {
        NotificationEventBus.flush { packageName, title, body, timestampMs ->
            deliverOrBuffer(packageName, title, body, timestampMs)
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
        // O JS só liga a captura depois de registrar o ouvinte: a partir daqui os eventos podem ser emitidos,
        // começando pelos que ficaram em espera (app fechado, área principal desmontada).
        deliverToJs = enabled
        if (enabled) flushBuffered()
    }

    /**
     * O JS deixou de escutar (a área principal saiu de cena) sem desligar a captura: o serviço continua lendo
     * e os eventos ficam no buffer até o próximo setCaptureEnabled(true).
     */
    @ReactMethod
    fun pauseDelivery() {
        deliverToJs = false
    }

    /** O grupo ativo mudou: o que esperava a ponte foi capturado para o grupo anterior e é descartado. */
    @ReactMethod
    fun discardBuffered() {
        NotificationEventBus.discardBuffered()
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
        deliverToJs = false
        reactContext.removeLifecycleEventListener(lifecycleListener)
        NotificationEventBus.listener = null
    }
}
