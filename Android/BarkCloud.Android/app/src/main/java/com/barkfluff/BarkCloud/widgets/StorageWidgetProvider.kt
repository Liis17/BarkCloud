package com.barkfluff.BarkCloud.widgets

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.text.format.Formatter
import android.widget.RemoteViews
import com.barkfluff.BarkCloud.MainActivity
import com.barkfluff.BarkCloud.R

class StorageWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        updateAll(context, manager, ids)
    }

    companion object {
        fun updateAll(context: Context, manager: AppWidgetManager, ids: IntArray) {
            ids.forEach { id ->
                manager.updateAppWidget(id, buildViews(context))
            }
        }

        private fun buildViews(context: Context): RemoteViews {
            val snapshot = StorageWidgetBridge.snapshot(context)
            val views = RemoteViews(context.packageName, R.layout.widget_storage)
            views.setTextViewText(R.id.storage_widget_title, context.getString(R.string.widget_storage_title))
            if (snapshot.hasData) {
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
            } else {
                views.setTextViewText(R.id.storage_widget_percent, "--")
                views.setTextViewText(R.id.storage_widget_summary, context.getString(R.string.widget_storage_no_data))
                views.setProgressBar(R.id.storage_widget_progress, 100, 0, false)
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
