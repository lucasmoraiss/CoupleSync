// AC-002: Expo Config Plugin — injects NotificationListenerService into AndroidManifest.xml
// and registers NotificationBridgePackage in MainApplication.kt during expo prebuild.
import {
  ConfigPlugin,
  withAndroidManifest,
  withMainApplication,
  AndroidConfig,
} from '@expo/config-plugins';

const SERVICE_CLASS = 'com.couplesync.app.NotificationCaptureService';
const BIND_PERMISSION = 'android.permission.BIND_NOTIFICATION_LISTENER_SERVICE';
const PACKAGE_IMPORT = 'import com.couplesync.app.NotificationBridgePackage';
const PACKAGE_ADD = 'packages.add(NotificationBridgePackage())';

const withNotificationListener: ConfigPlugin = (config) => {
  // ── Step 1: AndroidManifest.xml ──────────────────────────────────────────
  config = withAndroidManifest(config, (mod) => {
    const manifest = mod.modResults;

    // 1a. <uses-permission> for BIND_NOTIFICATION_LISTENER_SERVICE
    if (!manifest.manifest['uses-permission']) {
      manifest.manifest['uses-permission'] = [];
    }
    const permissions = manifest.manifest['uses-permission'] as Array<{
      $: Record<string, string>;
    }>;
    if (!permissions.some((p) => p.$?.['android:name'] === BIND_PERMISSION)) {
      permissions.push({ $: { 'android:name': BIND_PERMISSION } });
    }

    // 1b. <service> declaration inside <application>
    const mainApp = AndroidConfig.Manifest.getMainApplicationOrThrow(manifest);
    if (!mainApp.service) {
      mainApp.service = [];
    }
    const services = mainApp.service as Array<Record<string, unknown>>;
    if (
      !services.some(
        (s) =>
          (s.$ as Record<string, string>)?.['android:name'] === SERVICE_CLASS,
      )
    ) {
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

    return mod;
  });

  // ── Step 2: MainApplication.kt — register NotificationBridgePackage ──────
  config = withMainApplication(config, (mod) => {
    let contents = mod.modResults.contents;

    // Add import once, placed before the last Expo import line for safety
    if (!contents.includes(PACKAGE_IMPORT)) {
      contents = contents.replace(
        'import expo.modules.ReactNativeHostWrapper',
        `${PACKAGE_IMPORT}\nimport expo.modules.ReactNativeHostWrapper`,
      );
    }

    // Register package before 'return packages' in getPackages()
    if (!contents.includes(PACKAGE_ADD)) {
      contents = contents.replace(
        'return packages',
        `${PACKAGE_ADD}\n            return packages`,
      );
    }

    mod.modResults.contents = contents;
    return mod;
  });

  return config;
};

export default withNotificationListener;
