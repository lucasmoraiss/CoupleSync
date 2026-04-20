// AC-002 / AC-003: React Native module bridge that forwards notification events
// from NotificationCaptureService to the JS layer via DeviceEventEmitter.
package com.couplesync.app

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.provider.Settings
import android.text.TextUtils
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
                    // 'title' and 'body' are the original notification fields from the status bar
                    putString("title", title)
                    putString("body", body)
                    putDouble("timestampMs", timestampMs.toDouble())
                }
                reactContext
                    .getJSModule(DeviceEventManagerModule.RCTDeviceEventEmitter::class.java)
                    .emit("NotificationCaptured", params)
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
