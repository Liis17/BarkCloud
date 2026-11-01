package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cache.CacheScope
import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.json.JSONArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.IOException

class DeviceDeleteExecutorTest {

    @get:Rule
    val folder = TemporaryFolder()

    private val accountA = CacheScope("srv", "A")
    private val accountB = CacheScope("srv", "B")
    private val hash = "f".repeat(64)
    private val key = "image:1"

    private class FakeCloud : DeleteCloudPort {
        var state: CloudCopy? = CloudCopy.Absent
        var stateAvailable = true
        val deleted = mutableListOf<String>()
        val checkedHashes = mutableListOf<String>()
        var beforeAccess: (() -> Unit)? = null
        var failFor = emptySet<String>()
        var throwCancellationFor: String? = null

        override suspend fun copyState(hash: String): CloudCopy? {
            beforeAccess?.invoke()
            checkedHashes += hash
            return if (stateAvailable) state else null
        }

        override suspend fun deleteFile(fileId: String) {
            beforeAccess?.invoke()
            if (fileId == throwCancellationFor) throw CancellationException("screen gone")
            if (fileId in failFor) throw IOException("network")
            deleted += fileId
        }
    }

    private class FakeDevice(var cancelError: Throwable? = null) : DeviceCopyPort {
        val cancelled = mutableListOf<String>()
        val forgotten = mutableListOf<String>()
        val cancelAttempts = mutableListOf<String>()
        val forgetAttempts = mutableListOf<String>()
        var failForgetFor: String? = null
        var afterCancel: ((String) -> Unit)? = null
        override suspend fun cancelLiveJobs(mediaKey: String) {
            cancelAttempts += mediaKey
            cancelError?.let { throw it }
            cancelled += mediaKey
            afterCancel?.invoke(mediaKey)
        }

        override suspend fun forget(mediaKey: String) {
            forgetAttempts += mediaKey
            if (mediaKey == failForgetFor) throw IOException("media state unavailable")
            forgotten += mediaKey
        }
    }

    private class FakeVault(initial: Set<String> = emptySet()) : VaultPort {
        val ids = initial.toMutableSet()
        val removed = mutableListOf<String>()
        override fun fileIds(): Set<String> = ids
        override fun remove(fileId: String) {
            ids.remove(fileId)
            removed += fileId
        }
    }

    private class Env(
        val cloud: FakeCloud,
        val device: FakeDevice,
        val vault: FakeVault,
        var scope: CacheScope?,
        val storeFile: java.io.File,
        var unlocked: Boolean = false,
    ) {
        fun executor() = DeviceDeleteExecutor(
            cloud = cloud,
            device = device,
            vault = vault,
            store = DeviceDeleteOperationStore(storeFile),
            currentScope = { scope },
            hiddenUnlocked = { unlocked },
        )

        fun store() = DeviceDeleteOperationStore(storeFile)
    }

    private fun env(cloud: FakeCloud = FakeCloud(), scope: CacheScope? = accountA, vault: FakeVault = FakeVault(), device: FakeDevice = FakeDevice()) =
        Env(cloud, device, vault, scope, java.io.File(folder.root, "ops.json"))

    private fun plan(ids: List<String>) = planDeviceDelete(
        DeviceDeleteMode.EVERYWHERE,
        listOf(DeviceDeleteItem(key, CloudCopy.Present(ids.first(), ids))),
    )

    private fun twoPhotoPlan() = planDeviceDelete(
        DeviceDeleteMode.EVERYWHERE,
        listOf(
            DeviceDeleteItem(key, CloudCopy.Present("a", listOf("a"))),
            DeviceDeleteItem("image:2", CloudCopy.Absent),
        ),
    )

    @Test
    fun recreatedProcessRestoresAwaitingOperationWithoutInferringApproval() = runTest {
        val e = env()
        val executor = e.executor()
        executor.create(accountA, plan(listOf("a")), mapOf(key to hash), emptySet())

        // Новый процесс: читаем тот же файл. Одобрения нет — облако не трогаем.
        val restored = e.executor().pendingFor(accountA)
        assertEquals(listOf(DeleteOpPhase.AWAITING_SYSTEM), restored.map { it.phase })
        assertEquals(CloudRunResult.NotApproved, e.executor().runCloud(restored.single().id, allowHidden = false))
        assertEquals(emptyList<String>(), e.cloud.deleted)
    }

    @Test
    fun approvalIsPersistedBeforeQueueCancelFailureSoItIsNotLost() = runTest {
        val device = FakeDevice(cancelError = IOException("queue unavailable"))
        val e = env(device = device)
        val op = e.executor().create(accountA, plan(listOf("a")), mapOf(key to hash), emptySet())

        var failed = false
        try {
            e.executor().approve(op.id)
        } catch (_: IOException) {
            failed = true
        }

        assertTrue(failed)
        assertEquals(DeleteOpPhase.APPROVED, e.executor().pendingFor(accountA).single().phase)
    }

    @Test
    fun restartedApprovedLegacyOperationFinishesDeviceCleanupBeforeCloudDelete() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("a", listOf("a")) }
        val device = FakeDevice()
        val e = env(cloud = cloud, device = device)
        val op = e.executor().create(accountA, twoPhotoPlan(), mapOf(key to hash), emptySet())
        e.store().put(op.copy(phase = DeleteOpPhase.APPROVED))

        // Симулируем старую запись без checkpoint: процесс завершился сразу после сохранения APPROVED.
        val records = JSONArray(e.storeFile.readText())
        records.getJSONObject(0).remove("cancelledDevicePhotos")
        records.getJSONObject(0).remove("forgottenDevicePhotos")
        e.storeFile.writeText(records.toString())

        cloud.beforeAccess = {
            assertEquals(listOf(key, "image:2"), device.cancelled)
            assertEquals(listOf(key, "image:2"), device.forgotten)
        }
        val resumed = e.executor()
        assertEquals(emptyList<String>(), device.cancelled)

        val result = resumed.runCloud(op.id, allowHidden = false) as CloudRunResult.Finished

        assertEquals(listOf(key, "image:2"), device.cancelled)
        assertEquals(listOf(key, "image:2"), device.forgotten)
        assertEquals(listOf("a"), cloud.deleted)
        assertEquals(1, result.deleted)
    }

    @Test
    fun resumedApprovedOperationContinuesAtInterruptedDeviceCleanupCheckpoint() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("a", listOf("a")) }
        val device = FakeDevice().apply { failForgetFor = "image:2" }
        val e = env(cloud = cloud, device = device)
        val op = e.executor().create(accountA, twoPhotoPlan(), mapOf(key to hash), emptySet())

        var failed = false
        try {
            e.executor().approve(op.id)
        } catch (_: IOException) {
            failed = true
        }

        assertTrue(failed)
        assertEquals(emptyList<String>(), cloud.checkedHashes)
        assertEquals(listOf(key, "image:2"), device.cancelled)
        assertEquals(listOf(key), device.forgotten)
        device.failForgetFor = null
        cloud.beforeAccess = {
            assertEquals(listOf(key, "image:2"), device.cancelled)
            assertEquals(listOf(key, "image:2"), device.forgotten)
        }

        val result = e.executor().runCloud(op.id, allowHidden = false) as CloudRunResult.Finished

        assertEquals(1, device.cancelAttempts.count { it == key })
        assertEquals(1, device.cancelAttempts.count { it == "image:2" })
        assertEquals(1, device.forgetAttempts.count { it == key })
        assertEquals(2, device.forgetAttempts.count { it == "image:2" })
        assertEquals(listOf("a"), cloud.deleted)
        assertEquals(1, result.deleted)
    }

    @Test
    fun approvedOperationWithoutCloudTargetsStillCleansDeviceAfterRestart() = runTest {
        val e = env()
        val noCloudTarget = planDeviceDelete(
            DeviceDeleteMode.EVERYWHERE,
            listOf(DeviceDeleteItem(key, CloudCopy.Absent)),
        )
        val op = e.executor().create(accountA, noCloudTarget, emptyMap(), emptySet())
        e.store().put(op.copy(phase = DeleteOpPhase.APPROVED))

        val result = e.executor().runCloud(op.id, allowHidden = false) as CloudRunResult.Finished

        assertEquals(emptyList<String>(), e.cloud.checkedHashes)
        assertEquals(listOf(key), e.device.cancelled)
        assertEquals(listOf(key), e.device.forgotten)
        assertEquals(0, result.remaining)
        assertEquals(emptyList<DeleteOperation>(), e.executor().pendingFor(accountA))
    }

    @Test
    fun keepCloudModeStillCleansDeviceAfterRestartWithoutCloudCalls() = runTest {
        val e = env()
        val keepCloud = planDeviceDelete(
            DeviceDeleteMode.KEEP_CLOUD,
            listOf(DeviceDeleteItem(key, CloudCopy.Present("a", listOf("a")))),
        )
        val op = e.executor().create(accountA, keepCloud, emptyMap(), emptySet())
        e.store().put(op.copy(phase = DeleteOpPhase.APPROVED))

        val result = e.executor().runCloud(op.id, allowHidden = false) as CloudRunResult.Finished

        assertEquals(emptyList<String>(), e.cloud.checkedHashes)
        assertEquals(emptyList<String>(), e.cloud.deleted)
        assertEquals(listOf(key), e.device.cancelled)
        assertEquals(listOf(key), e.device.forgotten)
        assertEquals(0, result.remaining)
    }

    @Test
    fun scopeChangeDuringDeviceCleanupStopsBeforeTheNextSideEffect() = runTest {
        val e = env()
        val op = e.executor().create(accountA, twoPhotoPlan(), mapOf(key to hash), emptySet())
        e.device.afterCancel = { e.scope = accountB }

        assertEquals(null, e.executor().approve(op.id))
        assertEquals(listOf(key), e.device.cancelled)
        assertEquals(emptyList<String>(), e.device.forgotten)
        assertEquals(CloudRunResult.ScopeChanged, e.executor().runCloud(op.id, allowHidden = false))
        assertEquals(emptyList<String>(), e.cloud.checkedHashes)
    }

    @Test
    fun cancellationMidDeleteKeepsProgressAndNextRunDoesNotRepeatDoneIds() = runTest {
        val cloud = FakeCloud().apply {
            state = CloudCopy.Present("a", listOf("a", "b"))
            throwCancellationFor = "b"
        }
        val e = env(cloud = cloud)
        val op = e.executor().create(accountA, plan(listOf("a", "b")), mapOf(key to hash), emptySet())
        e.executor().approve(op.id)

        var cancelled = false
        try {
            e.executor().runCloud(op.id, allowHidden = false)
        } catch (_: CancellationException) {
            cancelled = true
        }
        assertTrue(cancelled)
        assertEquals(listOf("a"), cloud.deleted)

        cloud.throwCancellationFor = null
        val finished = e.executor().runCloud(op.id, allowHidden = false) as CloudRunResult.Finished
        assertEquals(listOf("a", "b"), cloud.deleted)
        assertEquals(1, finished.deleted)
        assertEquals(emptyList<DeleteOperation>(), e.executor().pendingFor(accountA))
    }

    @Test
    fun failedDeletionForAccountANeverDeletesIdenticalContentForAccountB() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("a", listOf("a")); failFor = setOf("a") }
        val e = env(cloud = cloud, scope = accountA)
        val op = e.executor().create(accountA, plan(listOf("a")), mapOf(key to hash), emptySet())
        e.executor().approve(op.id)
        e.executor().runCloud(op.id, allowHidden = false)
        assertEquals(1, e.executor().pendingFor(accountA).single().remainingCount)

        // Вход под аккаунтом B с тем же хешем: операция A не выполняется, облако B не трогается.
        e.scope = accountB
        cloud.failFor = emptySet()
        cloud.state = CloudCopy.Present("b-copy", listOf("b-copy"))
        assertEquals(emptyList<DeleteOperation>(), e.executor().pendingFor(accountB))
        assertEquals(CloudRunResult.ScopeChanged, e.executor().runCloud(op.id, allowHidden = false))
        assertEquals(emptyList<String>(), cloud.deleted)
    }

    @Test
    fun scopeChangeBetweenRequestsStopsBeforeNextDelete() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("a", listOf("a", "b")) }
        val e = env(cloud = cloud)
        val op = e.executor().create(accountA, plan(listOf("a", "b")), mapOf(key to hash), emptySet())
        e.executor().approve(op.id)
        // Аккаунт меняется сразу после первого удаления: второй запрос обязан не выполниться.
        val switching = object : DeleteCloudPort by cloud {
            override suspend fun deleteFile(fileId: String) {
                cloud.deleteFile(fileId)
                e.scope = accountB
            }
        }
        val executor = DeviceDeleteExecutor(
            cloud = switching,
            device = e.device,
            vault = e.vault,
            store = DeviceDeleteOperationStore(e.storeFile),
            currentScope = { e.scope },
            hiddenUnlocked = { e.unlocked },
        )

        assertEquals(CloudRunResult.ScopeChanged, executor.runCloud(op.id, allowHidden = false))
        assertEquals(listOf("a"), cloud.deleted)
    }

    @Test
    fun newMatchingCopyAfterConfirmationIsReportedNotDeleted() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("a", listOf("a", "new")) }
        val e = env(cloud = cloud)
        val op = e.executor().create(accountA, plan(listOf("a")), mapOf(key to hash), emptySet())
        e.executor().approve(op.id)

        val finished = e.executor().runCloud(op.id, allowHidden = false) as CloudRunResult.Finished

        assertEquals(listOf("a"), cloud.deleted)
        assertEquals(1, finished.newCopies)
    }

    @Test
    fun newlyHiddenTargetIsNotAddedToConfirmedSetOnRetry() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("a", listOf("a", "x")) }
        val vault = FakeVault(initial = setOf("x"))
        val e = env(cloud = cloud, vault = vault)
        // Подтверждена только «a»; «x» стала скрытой позже и не входит в подтверждение.
        val op = e.executor().create(accountA, plan(listOf("a")), mapOf(key to hash), emptySet())
        e.executor().approve(op.id)

        val result = e.executor().runCloud(op.id, allowHidden = false)

        assertEquals(listOf("a"), cloud.deleted)
        assertEquals(CloudRunResult.Finished(deleted = 1, reconciled = 0, newCopies = 1, failed = 0, remaining = 0), result)
    }

    @Test
    fun unauthorizedRetryNeverDeletesHiddenTarget() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("h", listOf("h")) }
        val vault = FakeVault(initial = setOf("h"))
        val e = env(cloud = cloud, vault = vault)
        val op = e.executor().create(accountA, plan(listOf("h")), mapOf(key to hash), setOf("h"))
        e.executor().approve(op.id)

        val result = e.executor().runCloud(op.id, allowHidden = false)

        assertEquals(CloudRunResult.NeedsUnlock(remaining = 1), result)
        assertEquals(emptyList<String>(), cloud.deleted)
        assertEquals(emptyList<String>(), vault.removed)
    }

    @Test
    fun lockBetweenRequestsStopsBeforeNextHiddenTarget() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("h1", listOf("h1", "h2")) }
        val vault = FakeVault(initial = setOf("h1", "h2"))
        val e = env(cloud = cloud, vault = vault, scope = accountA)
        e.unlocked = true
        val op = e.executor().create(accountA, plan(listOf("h1", "h2")), mapOf(key to hash), setOf("h1", "h2"))
        e.executor().approve(op.id)
        // Сессия блокируется сразу после первого удаления.
        val lockingCloud = object : DeleteCloudPort by cloud {
            override suspend fun deleteFile(fileId: String) {
                cloud.deleteFile(fileId)
                e.unlocked = false
            }
        }
        val lockingExecutor = DeviceDeleteExecutor(
            cloud = lockingCloud,
            device = e.device,
            vault = vault,
            store = DeviceDeleteOperationStore(e.storeFile),
            currentScope = { accountA },
            hiddenUnlocked = { e.unlocked },
        )

        val result = lockingExecutor.runCloud(op.id, allowHidden = true)

        assertEquals(listOf("h1"), cloud.deleted)
        assertEquals(CloudRunResult.NeedsUnlock(remaining = 1), result)
        assertEquals(listOf("h1"), vault.removed)
    }

    @Test
    fun alreadyAbsentOrTrashedConfirmedIdIsReconciledAndVaultCleanedWithoutDelete() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("b", listOf("b")) }
        val vault = FakeVault(initial = setOf("a"))
        val e = env(cloud = cloud, vault = vault)
        val op = e.executor().create(accountA, plan(listOf("a", "b")), mapOf(key to hash), setOf("a"))
        e.executor().approve(op.id)

        val finished = e.executor().runCloud(op.id, allowHidden = false) as CloudRunResult.Finished

        assertEquals(listOf("b"), cloud.deleted)
        assertEquals(1, finished.reconciled)
        assertEquals(listOf("a", "b"), vault.removed) // "a" сверено как отсутствующая, "b" удалена и проверена
    }

    @Test
    fun failedDeletionKeepsVaultEntryUntilVerified() = runTest {
        val cloud = FakeCloud().apply { state = CloudCopy.Present("h", listOf("h")); failFor = setOf("h") }
        val vault = FakeVault(initial = setOf("h"))
        val e = env(cloud = cloud, vault = vault)
        e.unlocked = true
        val op = e.executor().create(accountA, plan(listOf("h")), mapOf(key to hash), setOf("h"))
        e.executor().approve(op.id)

        e.executor().runCloud(op.id, allowHidden = true)

        assertEquals(emptyList<String>(), vault.removed)
        assertEquals(1, e.executor().pendingFor(accountA).single().remainingCount)
    }

    @Test
    fun denyRemovesAwaitingOperationAndLeavesCloudUntouched() = runTest {
        val cloud = FakeCloud()
        val e = env(cloud = cloud)
        val op = e.executor().create(accountA, plan(listOf("a")), mapOf(key to hash), emptySet())

        assertTrue(e.executor().deny(op.id))

        assertEquals(emptyList<DeleteOperation>(), e.executor().pendingFor(accountA))
        assertEquals(emptyList<String>(), cloud.deleted)
    }
}
