// AUTO-GENERATED from withNotificationListener.ts — do not edit directly.
// Expo Config Plugin resolvers require a .js file at runtime.
// AC-002: Injects NotificationListenerService into AndroidManifest.xml
// and registers NotificationBridgePackage in MainApplication.kt during expo prebuild.
'use strict';

const { withAndroidManifest, withMainApplication } = require('@expo/config-plugins');
const { AndroidConfig } = require('@expo/config-plugins');

const SERVICE_CLASS = 'com.couplesync.app.NotificationCaptureService';
const BIND_PERMISSION = 'android.permission.BIND_NOTIFICATION_LISTENER_SERVICE';
const PACKAGE_IMPORT = 'import com.couplesync.app.NotificationBridgePackage';
const PACKAGE_ADD = 'packages.add(NotificationBridgePackage())';

const withNotificationListener = (config) => {
  // ── Step 1: AndroidManifest.xml ──────────────────────────────────────────
  config = withAndroidManifest(config, (mod) => {
    const manifest = mod.modResults;

    // 1a. <uses-permission> for BIND_NOTIFICATION_LISTENER_SERVICE
    if (!manifest.manifest['uses-permission']) {
      manifest.manifest['uses-permission'] = [];
    }
    const permissions = manifest.manifest['uses-permission'];
    if (!permissions.some((p) => p.$?.['android:name'] === BIND_PERMISSION)) {
      permissions.push({ $: { 'android:name': BIND_PERMISSION } });
    }

    // 1b. <service> declaration inside <application>
    const mainApp = AndroidConfig.Manifest.getMainApplicationOrThrow(manifest);
    if (!mainApp.service) {
      mainApp.service = [];
    }
    const services = mainApp.service;
    if (!services.some((s) => s.$?.['android:name'] === SERVICE_CLASS)) {
      services.push({
        $: {
          'android:name': SERVICE_CLASS,
          'android:label': 'CoupleSync Notification Capture',
          'android:permission': BIND_PERMISSION,
          'android:exported': 'true',
        },
        'intent-filter': [
          {
            action: [
              {
                $: {
                  'android:name':
                    'android.service.notification.NotificationListenerService',
                },
              },
            ],
          },
        ],
      });
    }

    // Guard: throw if injection silently no-oped (SDK boilerplate changed)
    const injected = services.some((s) => s.$?.['android:name'] === SERVICE_CLASS);
    if (!injected) {
      throw new Error(
        '[withNotificationListener] Failed to inject NotificationCaptureService into AndroidManifest.xml. ' +
        'The manifest structure may have changed — check the plugin.',
      );
    }

    return mod;
  });

  // ── Step 2: MainApplication.kt — register NotificationBridgePackage ──────
  config = withMainApplication(config, (mod) => {
    let contents = mod.modResults.contents;

    // Add import once, placed before the last Expo import line for safety
    if (!contents.includes(PACKAGE_IMPORT)) {
      const replaced = contents.replace(
        'import expo.modules.ReactNativeHostWrapper',
        `${PACKAGE_IMPORT}\nimport expo.modules.ReactNativeHostWrapper`,
      );
      if (replaced === contents) {
        // Guard: marker not found — warn rather than silently skip
        console.warn(
          '[withNotificationListener] Could not find "import expo.modules.ReactNativeHostWrapper" in MainApplication.kt. ' +
          'NotificationBridgePackage import was NOT injected. Verify the Expo SDK boilerplate.',
        );
      }
      contents = replaced;
    }

    // Register package before 'return packages' in getPackages()
    if (!contents.includes(PACKAGE_ADD)) {
      const replaced = contents.replace(
        'return packages',
        `${PACKAGE_ADD}\n            return packages`,
      );
      if (replaced === contents) {
        console.warn(
          '[withNotificationListener] Could not find "return packages" in MainApplication.kt. ' +
          'NotificationBridgePackage was NOT registered. Verify the Expo SDK boilerplate.',
        );
      }
      contents = replaced;
    }

    mod.modResults.contents = contents;
    return mod;
  });

  return config;
};

module.exports = withNotificationListener;
