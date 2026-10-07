package com.couplesync.e2estub;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.os.Bundle;

/**
 * Posts one made-up purchase notification and closes at once (no screen), so the app under test stays in front.
 * Started by scripts/app-e2e-run-flows.sh:
 *   adb shell am start -W -n com.nu.production/com.couplesync.e2estub.PostNotificationActivity
 *
 * The text is in a format that the app's parser turns into an expense (pattern "nu-purchase" in
 * mobile/src/modules/integrations/notification-capture/notification-patterns.json). The merchant and the amount
 * are the ones the flow 07-captura-de-notificacao-conferir.yaml looks for: change them together.
 * Invented data: no real person, card or purchase.
 */
public final class PostNotificationActivity extends Activity {
    private static final String CHANNEL_ID = "e2e";
    private static final String TITLE = "Compra aprovada";
    private static final String TEXT = "Compra de R$ 37,50 aprovada em PADARIA E2E";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(
                new NotificationChannel(CHANNEL_ID, "E2E", NotificationManager.IMPORTANCE_DEFAULT));
        Notification notification = new Notification.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.stat_notify_more)
                .setContentTitle(TITLE)
                .setContentText(TEXT)
                .build();
        manager.notify(1, notification);
        finish();
    }
}
