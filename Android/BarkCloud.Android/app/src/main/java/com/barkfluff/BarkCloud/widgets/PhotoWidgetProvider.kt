package com.barkfluff.BarkCloud.widgets

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.view.View
import android.widget.RemoteViews
import com.barkfluff.BarkCloud.MainActivity
import com.barkfluff.BarkCloud.R

class PhotoWidgetProvider : AppWidgetProvider() {
    override fun onEnabled(context: Context) = WidgetRefresh.ensurePeriodic(context)

    override fun onDisabled(context: Context) = WidgetRefresh.cancelPeriodicIfNoWidgets(context)

    override fun onDeleted(context: Context, appWidgetIds: IntArray) {
        appWidgetIds.forEach { PhotoWidgetStore.remove(context, it) }
    }

    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        updateAll(context, manager, ids)
        WidgetRefresh.ensurePeriodic(context)
        WidgetRefresh.refreshNow(context)
    }

    companion object {
        fun refreshAll(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            updateAll(context, manager, manager.getAppWidgetIds(ComponentName(context, PhotoWidgetProvider::class.java)))
        }

        private fun updateAll(context: Context, manager: AppWidgetManager, ids: IntArray) {
            ids.forEach { id -> manager.updateAppWidget(id, buildViews(context, id)) }
        }

        private fun buildViews(context: Context, widgetId: Int): RemoteViews {
            val views = RemoteViews(context.packageName, R.layout.widget_photo)
            val config = PhotoWidgetStore.config(context, widgetId)
            val frame = PhotoWidgetStore.currentFrame(context, widgetId)
            val bitmap = frame?.let { BitmapFactory.decodeFile(it.path) }

            if (bitmap != null && config != null) {
                views.setImageViewBitmap(R.id.photo_widget_image, bitmap)
                views.setViewVisibility(R.id.photo_widget_message, View.GONE)
                views.setTextViewText(R.id.photo_widget_caption, config.albumName)
            } else {
                views.setViewVisibility(R.id.photo_widget_message, View.VISIBLE)
                views.setTextViewText(R.id.photo_widget_message, messageFor(context, widgetId, config != null))
                views.setTextViewText(R.id.photo_widget_caption, "")
            }

            // requestCode = widgetId: extras не участвуют в сравнении PendingIntent, поэтому без
            // уникального кода все виджеты открыли бы настройку одного и того же id.
            val intent = if (config == null) {
                Intent(context, PhotoWidgetConfigActivity::class.java)
                    .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId)
            } else {
                Intent(Intent.ACTION_VIEW, Uri.parse("barkcloud://albums"), context, MainActivity::class.java)
            }
            val pending = PendingIntent.getActivity(
                context,
                widgetId,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            views.setOnClickPendingIntent(R.id.photo_widget_root, pending)
            return views
        }

        private fun messageFor(context: Context, widgetId: Int, hasAlbum: Boolean): String = context.getString(
            when {
                !hasAlbum -> R.string.widget_photo_no_album
                !WidgetRefresh.isSignedIn(context) -> R.string.widget_login_required
                PhotoWidgetStore.hasFetched(context, widgetId) -> R.string.widget_photo_empty
                else -> R.string.widget_photo_loading
            },
        )
    }
}
