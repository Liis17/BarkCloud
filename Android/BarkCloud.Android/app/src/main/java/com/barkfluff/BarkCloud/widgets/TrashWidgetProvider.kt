package com.barkfluff.BarkCloud.widgets

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.RemoteViews
import com.barkfluff.BarkCloud.MainActivity
import com.barkfluff.BarkCloud.R

class TrashWidgetProvider : AppWidgetProvider() {
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
            updateAll(context, manager, manager.getAppWidgetIds(ComponentName(context, TrashWidgetProvider::class.java)))
        }

        private fun updateAll(context: Context, manager: AppWidgetManager, ids: IntArray) {
            ids.forEach { id -> manager.updateAppWidget(id, buildViews(context)) }
        }

        private fun buildViews(context: Context): RemoteViews {
            val views = RemoteViews(context.packageName, R.layout.widget_trash)
            val snapshot = TrashWidgetBridge.snapshot(context)
            when {
                snapshot == null -> {
                    views.setTextViewText(R.id.trash_widget_count, "--")
                    views.setTextViewText(
                        R.id.trash_widget_deadline,
                        context.getString(
                            if (WidgetRefresh.isSignedIn(context)) R.string.widget_storage_no_data
                            else R.string.widget_login_required,
                        ),
                    )
                }
                snapshot.count == 0 -> {
                    views.setTextViewText(R.id.trash_widget_count, context.getString(R.string.widget_trash_empty))
                    views.setTextViewText(R.id.trash_widget_deadline, "")
                }
                else -> {
                    views.setTextViewText(
                        R.id.trash_widget_count,
                        context.resources.getQuantityString(R.plurals.widget_trash_count, snapshot.count, snapshot.count),
                    )
                    views.setTextViewText(R.id.trash_widget_deadline, deadlineText(context, snapshot.oldestPurgeAtMillis))
                }
            }

            val intent = Intent(Intent.ACTION_VIEW, Uri.parse("barkcloud://trash"), context, MainActivity::class.java)
            val pending = PendingIntent.getActivity(
                context,
                1,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            views.setOnClickPendingIntent(R.id.trash_widget_root, pending)
            return views
        }

        private fun deadlineText(context: Context, purgeAtMillis: Long?): String {
            if (purgeAtMillis == null) return ""
            val res = context.resources
            return when (val deadline = trashDeadline(purgeAtMillis, System.currentTimeMillis())) {
                TrashDeadline.Soon -> context.getString(R.string.widget_trash_soon)
                is TrashDeadline.Hours -> res.getQuantityString(R.plurals.widget_trash_hours, deadline.hours, deadline.hours)
                is TrashDeadline.Days -> res.getQuantityString(R.plurals.widget_trash_days, deadline.days, deadline.days)
            }
        }
    }
}
