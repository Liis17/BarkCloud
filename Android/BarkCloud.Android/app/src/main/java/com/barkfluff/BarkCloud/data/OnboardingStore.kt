package com.barkfluff.BarkCloud.data

import android.content.Context

class OnboardingStore(context: Context) {
    private val prefs = context.getSharedPreferences("barkcloud_onboarding", Context.MODE_PRIVATE)
    val completed: Boolean get() = prefs.getBoolean("completed", false)
    fun complete() { prefs.edit().putBoolean("completed", true).apply() }
}
