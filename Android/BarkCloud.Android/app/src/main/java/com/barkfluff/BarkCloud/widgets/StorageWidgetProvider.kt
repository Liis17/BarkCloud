package com.barkfluff.BarkCloud.widgets

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.text.format.Formatter
import android.widget.RemoteViews
import com.barkfluff.BarkCloud.MainActivity
import com.barkfluff.BarkCloud.R

class StorageWidgetProvider : AppWidgetProvider() {
    override fun onEnabled(context: Context) = WidgetRefresh.ensurePeriodic(context)

    override fun onDisabled(context: Context) = WidgetRefresh.cancelPeriodicIfNoWidgets(context)

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        updateAll(context, manager, ids)
        WidgetRefresh.ensurePeriodic(context)
        WidgetRefresh.refreshNow(context)
    }

    companion object {
        fun refreshAll(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            updateAll(context, manager, manager.getAppWidgetIds(ComponentName(context, StorageWidgetProvider::class.java)))
        }

        private fun updateAll(context: Context, manager: AppWidgetManager, ids: IntArray) {
            ids.forEach { id -> manager.updateAppWidget(id, buildViews(context)) }
        }

        private fun buildViews(context: Context): RemoteViews {
            val snapshot = StorageWidgetBridge.snapshot(context)
            val views = RemoteViews(context.packageName, R.layout.widget_storage)
            views.setTextViewText(R.id.storage_widget_title, context.getString(R.string.widget_storage_title))
            when {
                snapshot.hasData && snapshot.hasLimit -> {
                    views.setTextViewText(R.id.storage_widget_percent, "${snapshot.percent}%")
                    views.setTextViewText(
                        R.id.storage_widget_summary,
                        context.getString(
                            R.string.widget_storage_summary,
                            Formatter.formatFileSize(context, snapshot.used),
                            Formatter.formatFileSize(context, snapshot.limit),
                        ),
                    )
                    views.setProgressBar(R.id.storage_widget_progress, 100, snapshot.percent, false)
                }
                snapshot.hasData -> {
                    // Квоты нет (бэкенд отдаёт LimitBytes = null → limit 0): показываем только занятое.
                    views.setTextViewText(R.id.storage_widget_percent, Formatter.formatFileSize(context, snapshot.used))
                    views.setTextViewText(R.id.storage_widget_summary, context.getString(R.string.widget_storage_unlimited))
                    views.setProgressBar(R.id.storage_widget_progress, 100, 0, false)
                }
                else -> {
                    views.setTextViewText(R.id.storage_widget_percent, "--")
                    views.setTextViewText(
                        R.id.storage_widget_summary,
                        context.getString(
                            if (WidgetRefresh.isSignedIn(context)) R.string.widget_storage_no_data
                            else R.string.widget_login_required,
                        ),
                    )
                    views.setProgressBar(R.id.storage_widget_progress, 100, 0, false)
                }
            }

            val intent = Intent(Intent.ACTION_VIEW, Uri.parse("barkcloud://settings"), context, MainActivity::class.java)
            val pending = PendingIntent.getActivity(
                context,
                0,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            views.setOnClickPendingIntent(R.id.storage_widget_root, pending)
            return views
        }
    }
}
