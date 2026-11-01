package com.barkfluff.BarkCloud.ui.vault

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Гейт не получает данных о глобальной блокировке приложения: выбор зависит только от состояния Vault. */
class VaultViewerGateTest {
    @Test
    fun viewerIsKeptOnlyWhileVaultIsUnlockedRegardlessOfAppLock() {
        assertEquals("photo", vaultViewerSelection(VaultLockState.UNLOCKED, "photo"))
        assertNull(vaultViewerSelection(VaultLockState.LOCKED, "photo"))
        assertNull(vaultViewerSelection(VaultLockState.UNLOCKING, "photo"))
    }

    @Test
    fun relockedViewerDoesNotReopenWhenVaultIsUnlockedAgain() {
        var viewer: String? = "photo"
        viewer = vaultViewerSelection(VaultLockState.LOCKED, viewer)
        viewer = vaultViewerSelection(VaultLockState.UNLOCKED, viewer)

        assertNull(viewer)
    }
}
