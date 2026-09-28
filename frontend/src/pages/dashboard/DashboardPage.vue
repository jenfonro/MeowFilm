<template>
  <div class="app-shell adm-flex adm-min-h-screen">
    <aside
      class="sidebar dashboard-sidebar adm-flex-shrink-0 adm-bg-white-40 adm-backdrop-blur-xl adm-transition-all adm-duration-300 adm-border-r adm-border-gray-200-50 adm-shadow-lg adm-w-64 adm-p-4 adm-pt-6"
      style="backdrop-filter: blur(20px); -webkit-backdrop-filter: blur(20px)"
    >
      <div class="adm-text-xl adm-font-bold adm-text-green-600 adm-mb-6 adm-flex adm-items-center adm-gap-2">
        <svg
          xmlns="http://www.w3.org/2000/svg"
          width="24"
          height="24"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="lucide lucide-layout-dashboard"
        >
          <rect width="18" height="18" x="3" y="3" rx="2"></rect>
          <path d="M3 9h18"></path>
          <path d="M9 21V9"></path>
        </svg>
        管理后台
      </div>

      <nav class="adm-space-y-1">
        <a
          href="/"
          class="nav-item group adm-flex adm-items-center adm-rounded-lg adm-px-3 adm-py-2 adm-pl-4 adm-text-gray-700 adm-hover-bg-gray-100-30 adm-hover-text-green-600 adm-gap-3 adm-justify-start adm-transition-colors adm-duration-200 adm-min-h-40"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            width="20"
            height="20"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            class="lucide lucide-house adm-h-5 adm-w-5 adm-text-gray-500 adm-group-hover-text-green-600"
          >
            <path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8"></path>
            <path d="M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"></path>
          </svg>
          <span class="nav-label">回到首页</span>
        </a>

        <div v-if="isAdmin" class="adm-space-y-1">
          <a
            v-for="item in adminNavItems"
            :key="item.key"
            :data-dashboard="item.key"
            :data-active="activeNavKey === item.key"
            class="dashboard-nav nav-item group adm-flex adm-items-center adm-rounded-lg adm-px-3 adm-py-2 adm-pl-4 adm-text-gray-700 adm-hover-bg-gray-100-30 adm-hover-text-green-600 adm-data-active-bg-green-500-20 adm-data-active-text-green-700 adm-gap-3 adm-justify-start adm-transition-colors adm-duration-200 adm-min-h-40"
            href="#"
            @click.prevent="activeNavKey = item.key"
          >
            <component :is="item.icon" class="lucide adm-h-5 adm-w-5 adm-text-gray-500 adm-group-hover-text-green-600" />
            <span class="nav-label">{{ item.label }}</span>
          </a>
        </div>
      </nav>
    </aside>

    <div class="adm-flex-1 adm-min-w-0 adm-flex adm-flex-col">
      <div class="adm-flex-1 adm-min-w-0 adm-overflow-y-auto adm-p-6 adm-space-y-6">
        <section
          v-if="isAdmin && activeNavKey === 'site'"
          id="dashboardSite"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <SettingsIcon class="lucide lucide-settings adm-h-5 adm-w-5 adm-text-gray-600" />
            全局设置
          </div>

          <form @submit.prevent="saveGlobalSettings">
            <div class="dashboard-card adm-space-y-4">
              <div class="adm-flex adm-items-center adm-gap-3">
                <div class="adm-text-sm adm-font-semibold adm-text-gray-700">站点设置</div>
              </div>

              <div>
                <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">站点名称</label>
                <input
                  v-model="siteForm.siteName"
                  class="tv-field"
                  placeholder="站点名称"
                  autocomplete="off"
                />
              </div>

              <div class="adm-relative">
                <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">搜索展示设置</label>
                <div class="ui-selectbox ui-selectbox--compact" ref="searchDisplayDropdownRef">
                  <button
                    type="button"
                    class="ui-selectbox__trigger"
                    :disabled="siteLoading || globalSaving"
                    @click="toggleSearchDisplayDropdown"
                  >
                    {{ currentSearchDisplayLabel }}
                  </button>
                  <div
                    v-if="searchDisplayDropdownOpen"
                    class="ui-selectbox__menu"
                  >
                    <div
                      v-for="option in searchDisplayOptions"
                      :key="option.value"
                      class="ui-selectbox__option"
                      :class="{ 'is-active': siteForm.searchDisplayMode === option.value }"
                      @click="selectSearchDisplayMode(option.value)"
                    >
                      {{ option.label }}
                    </div>
                  </div>
                </div>
                <div
                  v-if="searchDisplayModeError"
                  class="adm-text-xs adm-mt-1 adm-text-red-600"
                >
                  {{ searchDisplayModeError }}
                </div>
              </div>
            </div>

            <div class="dashboard-card adm-space-y-4 adm-mt-6">
              <div class="adm-flex adm-items-center adm-gap-3">
                <div class="adm-text-sm adm-font-semibold adm-text-gray-700">网盘接口代理设置</div>
              </div>
              <div class="adm-space-y-1">
                <div class="adm-text-xs adm-text-gray-500">仅影响天翼189/夸克/UC/移动云盘/百度等网盘接口请求。</div>
              </div>

              <div class="adm-space-y-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">启用代理</div>
                <div>
                  <label class="enable-switch" title="网盘接口代理启用">
                    <input
                      v-model="siteForm.netdiskProxyEnabled"
                      type="checkbox"
                    />
                    <span class="enable-slider"></span>
                  </label>
                </div>
              </div>

              <div>
                <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">代理地址</label>
                <input
                  v-model="siteForm.netdiskProxyUrl"
                  class="tv-field"
                  placeholder="http://127.0.0.1:7890"
                  autocomplete="off"
                  :readonly="!siteForm.netdiskProxyEnabled"
                  :class="{ 'tv-field-readonly': !siteForm.netdiskProxyEnabled }"
                />
              </div>
            </div>
          </form>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">备份/恢复</div>
            </div>
            <div class="adm-text-xs adm-text-gray-500">
              导出/导入后台配置（用于删库后快速恢复；导入会忽略未知字段）。
            </div>
            <div class="adm-flex adm-items-center adm-gap-2">
              <button
                type="button"
                class="btn-green"
                :disabled="backupExporting || backupImporting"
                @click="exportBackup"
              >
                {{ backupExporting ? '导出中' : '备份' }}
              </button>
              <button
                type="button"
                class="btn-ghost-blue"
                :disabled="backupExporting || backupImporting"
                @click="pickImportFile"
              >
                {{ backupImporting ? '导入中' : '恢复' }}
              </button>
              <input
                ref="backupImportFileRef"
                type="file"
                accept="application/json,.json"
                class="hidden"
                @change="importBackup"
              />
            </div>
          </div>

          <div class="adm-pt-1 adm-flex adm-justify-start">
            <button
              id="globalSettingsSave"
              type="button"
              class="btn-green"
              :disabled="siteLoading || globalSaving"
              @click="saveGlobalSettings"
            >
              {{ globalSaving ? '保存中' : '保存' }}
            </button>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'user'"
          id="dashboardUser"
          class="dashboard-panel adm-space-y-4"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <UserIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            用户管理
          </div>

          <div class="stat-card">
            <div class="stat-main">{{ userCount }}</div>
            <div class="stat-label">总用户数</div>
          </div>

          <div class="dashboard-card">
            <div class="user-panel__header">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700 adm-flex adm-items-center adm-gap-2">
                用户列表
                <button type="button" class="btn-green" @click="toggleAddUserForm">
                  {{ addUserFormOpen ? '取消' : '添加用户' }}
                </button>
              </div>
            </div>

            <form
              v-if="addUserFormOpen"
              class="user-add-form"
              @submit.prevent="submitAddUser"
            >
              <div class="user-form-grid">
                <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">用户名：</span>
                <input
                  v-model="addUserForm.username"
                  class="tv-field"
                  placeholder="用户名"
                  autocomplete="off"
                />

                <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">密码：</span>
                <input
                  v-model="addUserForm.password"
                  type="password"
                  class="tv-field user-field-success"
                  placeholder="密码"
                  autocomplete="new-password"
                />
              </div>
              <div class="user-form-actions">
                <button type="submit" class="btn-add" :class="{ active: canSubmitAddUser }" :disabled="!canSubmitAddUser || userAddSubmitting">
                  {{ userAddSubmitting ? '添加中' : '添加' }}
                </button>
              </div>
            </form>

            <div class="user-table-wrap">
              <div class="user-table-shell">
                <table class="user-table">
                  <thead class="table-head">
                    <tr>
                      <th>用户名</th>
                      <th>角色</th>
                      <th>状态</th>
                      <th>操作</th>
                    </tr>
                  </thead>
                  <tbody>
                    <template v-for="user in users" :key="user.username">
                      <tr>
                        <td class="user-table__name">{{ user.username }}</td>
                        <td>
                          <span v-if="user.role === 'admin'" class="tag-yellow">管理员</span>
                          <span v-else class="tag-gray">用户</span>
                        </td>
                        <td>
                          <span v-if="user.status === 'active'" class="tag-green">正常</span>
                          <span v-else-if="user.status === 'banned'" class="tag-red">封禁</span>
                          <span v-else class="tag-gray">{{ user.status }}</span>
                        </td>
                        <td>
                          <div class="action-group">
                            <button type="button" class="action-btn blue" @click="openEditUser(user)">修改</button>
                            <button
                              v-if="user.role !== 'admin'"
                              type="button"
                              class="action-btn rose"
                              :disabled="userRowBusy === user.username"
                              @click="toggleUserStatus(user)"
                            >
                              {{ user.status === 'active' ? '封禁' : '解封' }}
                            </button>
                            <button
                              v-if="user.role !== 'admin'"
                              type="button"
                              class="action-btn red"
                              :disabled="userRowBusy === user.username"
                              @click="removeUser(user)"
                            >
                              删除
                            </button>
                          </div>
                        </td>
                      </tr>
                      <tr v-if="editingUsername === user.username" class="user-edit-row">
                        <td colspan="4">
                          <div class="user-edit-box">
                            <div class="user-form-grid">
                              <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">新用户名：</span>
                              <input
                                v-model="editUserForm.newUsername"
                                type="text"
                                class="tv-field"
                                placeholder="新用户名"
                                autocomplete="off"
                              />

                              <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">新密码：</span>
                              <input
                                v-model="editUserForm.newPassword"
                                type="password"
                                class="tv-field"
                                placeholder="新密码"
                                autocomplete="new-password"
                              />
                            </div>
                            <div class="action-group user-edit-actions">
                              <button
                                type="button"
                                class="action-btn green"
                                :disabled="!canSubmitUserEdit || userEditSubmitting"
                                @click="submitEditUser"
                              >
                                {{ userEditSubmitting ? '保存中' : '确定' }}
                              </button>
                              <button type="button" class="action-btn gray" :disabled="userEditSubmitting" @click="closeEditUser">
                                取消
                              </button>
                            </div>
                          </div>
                        </td>
                      </tr>
                    </template>
                    <tr v-if="!users.length">
                      <td colspan="4" class="user-table__empty">暂无用户</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'pan'"
          id="dashboardPan"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <CloudIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            网盘设置
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-2">
              <div ref="panTabsRef" class="adm-flex adm-items-center adm-gap-2 adm-overflow-x-auto adm-pb-1 adm-min-w-0 adm-flex-1">
                <button
                  v-for="item in panSettingDefs"
                  :key="item.key"
                  type="button"
                  class="adm-px-3 adm-rounded-full adm-text-sm adm-font-medium adm-transition-colors"
                  :style="activePanSettingKey === item.key
                    ? 'padding-top: 6px; padding-bottom: 6px; white-space: nowrap; appearance: none; -webkit-appearance: none; cursor: pointer; border-radius: 9999px; background: rgba(34,197,94,0.20); border: 1px solid rgba(34,197,94,0.30); color: #15803d;'
                    : 'padding-top: 6px; padding-bottom: 6px; white-space: nowrap; appearance: none; -webkit-appearance: none; cursor: pointer; border-radius: 9999px; background: transparent; border: 1px solid transparent; color: #374151;'"
                  @click="selectPanSetting(item.key)"
                >
                  {{ item.name }}
                </button>
              </div>
            </div>

            <div class="adm-min-h-320">
              <template v-if="activePanSettingDef">
                <div
                  v-if="isCookiePan(activePanSettingDef) || isAuthorizationPan(activePanSettingDef)"
                  class="adm-flex adm-flex-col adm-items-start adm-gap-3"
                  style="width: 100%;"
                >
                  <div
                    v-if="supportsQrLogin(activePanSettingDef.key) && activeQrState.imageUrl"
                    class="adm-w-full adm-flex adm-items-center adm-justify-center"
                    style="width: 100%; justify-content: center;"
                  >
                    <img
                      :src="activeQrState.imageUrl"
                      :alt="`${activePanSettingDef.name} qrcode`"
                      style="display: block; width: 220px; height: 220px; margin: 0 auto; object-fit: contain; border-radius: 12px; background: #fff; border: 1px solid rgba(0,0,0,0.10);"
                    />
                  </div>

                  <textarea
                    v-model="activePanTextValue"
                    class="tv-field"
                    rows="3"
                    style="width: 100%; resize: vertical;"
                    :placeholder="isAuthorizationPan(activePanSettingDef) ? `请输入${activePanSettingDef.name} Authorization` : `请输入${activePanSettingDef.name} Cookie`"
                  ></textarea>

                  <div
                    :style="supportsQrLogin(activePanSettingDef.key)
                      ? 'position: relative; display: flex; justify-content: flex-start; align-items: center; width: 100%;'
                      : 'display: flex; justify-content: center; align-items: center; width: 100%;'"
                  >
                    <button
                      v-if="supportsQrLogin(activePanSettingDef.key)"
                      type="button"
                      class="btn-ghost-blue"
                      :disabled="activeQrButtonBusy"
                      @click="startActivePanQrLogin"
                    >
                      {{ activeQrButtonLabel }}
                    </button>
                    <button
                      type="button"
                      class="btn-green"
                      :style="supportsQrLogin(activePanSettingDef.key)
                        ? 'position: absolute; left: 50%; transform: translateX(-50%);'
                        : ''"
                      :disabled="panSaveBusy"
                      @click="saveActivePanText"
                    >
                      {{ panSaveBusy ? '保存中' : '保存' }}
                    </button>
                  </div>
                </div>

                <div
                  v-else-if="isTvPan(activePanSettingDef)"
                  class="adm-space-y-4"
                  :style="shouldUseFullWidthPanInputs(activePanSettingDef) ? '' : 'max-width: 640px;'"
                >
                  <div>
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">refresh_token</label>
                    <input v-model="activePanTv.refresh_token" class="tv-field" autocomplete="off" />
                  </div>
                  <div>
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">device_id</label>
                    <input v-model="activePanTv.device_id" class="tv-field" autocomplete="off" />
                  </div>
                  <div>
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">access_token（可空）</label>
                    <input v-model="activePanTv.access_token" class="tv-field" autocomplete="off" />
                  </div>
                  <div>
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">access_token_exp_at（可空）</label>
                    <input v-model="activePanTv.access_token_exp_at" class="tv-field" autocomplete="off" />
                  </div>
                  <div class="adm-flex adm-justify-start adm-items-center adm-gap-3 adm-mt-3">
                    <button
                      type="button"
                      class="btn-green"
                      :disabled="panSaveBusy"
                      @click="saveActivePanTv"
                    >
                      {{ panSaveBusy ? '保存中' : '保存' }}
                    </button>
                  </div>
                </div>

                <div
                  v-else
                  class="adm-space-y-4"
                  :style="shouldUseFullWidthPanInputs(activePanSettingDef) ? '' : 'max-width: 640px;'"
                >
                  <div>
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">账号</label>
                    <input v-model="activePanAccount.username" class="tv-field" autocomplete="off" />
                  </div>
                  <div>
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">密码</label>
                    <input v-model="activePanAccount.password" type="password" class="tv-field" autocomplete="new-password" />
                  </div>
                  <div v-if="activePanSettingDef.key === '189'">
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">Cookie（可空）</label>
                    <textarea v-model="activePanAccount.cookie" class="tv-field" rows="4" style="width: 100%; resize: vertical;"></textarea>
                  </div>
                  <div class="adm-flex adm-justify-start adm-items-center adm-gap-3 adm-mt-3">
                    <button
                      type="button"
                      class="btn-green"
                      :disabled="panSaveBusy"
                      @click="saveActivePanAccount"
                    >
                      {{ panSaveBusy ? '保存中' : '保存' }}
                    </button>
                  </div>
                </div>
              </template>
            </div>

          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'interface'"
          id="dashboardInterface"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <LinkIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            接口设置
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">CatPawRunner 设置</div>
            </div>

            <div class="adm-space-y-2">
              <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap adm-justify-start">
                <button type="button" class="btn-green" :disabled="interfaceLoading || catSaving || catDeleting" @click="toggleCatServerAddMode">
                  {{ catServerAddMode ? '取消' : '添加服务器' }}
                </button>
              </div>
              <div class="adm-flex adm-items-center adm-gap-3">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">服务器选择</div>
                <div ref="catServerDropdownRef" class="ui-selectbox ui-selectbox--compact tv-cpo-server-select">
                  <button
                    type="button"
                    class="ui-selectbox__trigger"
                    :disabled="interfaceLoading || catSaving || catDeleting || !catServerOptions.length"
                    @click="toggleCatServerDropdown"
                  >
                    {{ selectedCatServerLabel }}
                  </button>
                  <div v-if="catServerDropdownOpen" class="ui-selectbox__menu">
                    <div
                      v-for="option in catServerOptions"
                      :key="option.key"
                      class="ui-selectbox__option"
                      :class="{ 'is-active': catSelectedServerKey === option.key }"
                      @click="selectCatServer(option.key)"
                    >
                      {{ option.label }}
                    </div>
                  </div>
                </div>
                <button
                  v-if="!catServerAddMode && catSelectedServerKey && catSelectedServerKey !== '__new__'"
                  type="button"
                  class="btn-ghost-red"
                  :disabled="catDeleting || catSaving"
                  @click="deleteCatServer"
                >
                  {{ catDeleting ? '删除中' : '删除' }}
                </button>
              </div>
            </div>

              <div v-if="catServerAddMode || catSelectedServerKey" class="adm-space-y-4">
                <div>
                  <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">服务器名称</label>
                  <input v-model="catForm.name" class="tv-field" autocomplete="off" />
                </div>
                <div>
                  <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">CatPawRunner 接口地址</label>
                  <input v-model="catForm.apiBase" class="tv-field" placeholder="http://127.0.0.1:9988" autocomplete="off" />
                </div>
                <div v-if="showCatSyncFromServerRow && catSyncFromServerOptions.length" class="adm-flex adm-items-center adm-gap-2 adm-flex-wrap adm-text-sm adm-text-gray-700">
                  <span class="adm-whitespace-nowrap">从</span>
                  <div ref="catSyncFromServerDropdownRef" class="ui-selectbox ui-selectbox--compact tv-cpo-server-select">
                    <button
                      type="button"
                      class="ui-selectbox__trigger"
                      :disabled="catRemoteLoading || catSaving || !catSyncFromServerOptions.length"
                      @click="toggleCatSyncFromServerDropdown"
                    >
                      {{ selectedCatSyncFromServerLabel }}
                    </button>
                    <div v-if="catSyncFromServerDropdownOpen" class="ui-selectbox__menu">
                      <div
                        v-for="option in catSyncFromServerOptions"
                        :key="option.key"
                        class="ui-selectbox__option"
                        :class="{ 'is-active': catSyncFromServerKey === option.key }"
                        @click="selectCatSyncFromServer(option.key)"
                      >
                        {{ option.label }}
                      </div>
                    </div>
                  </div>
                  <span class="adm-whitespace-nowrap">获取配置并同步</span>
                </div>
                <div v-if="showCatSettingsExtras && normalizedCatApiBase" class="adm-text-xs adm-text-gray-500">
                  当前接口：{{ normalizedCatApiBase }}
                </div>
                <div v-if="showCatSettingsExtras" class="adm-space-y-3">
                  <div class="adm-flex adm-items-center adm-gap-2">
                    <label class="adm-text-sm adm-font-medium adm-text-gray-700">配置列表</label>
                    <button type="button" class="btn-green" :disabled="catSaving || catRemoteLoading" @click="openCatConfigEditorForCreate">
                      添加
                    </button>
                  </div>
                  <div class="tv-panel tv-cpo-config-shell">
                    <div v-if="catConfigEditorOpen" class="tv-panel tv-cpo-config-editor tv-cpo-online-config-editor adm-mb-3">
                      <div class="user-form-grid">
                        <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">名称：</span>
                        <input v-model="catConfigEditorForm.name" class="tv-field" placeholder="名称" autocomplete="off" />

                        <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">配置地址：</span>
                        <input v-model="catConfigEditorForm.url" class="tv-field" placeholder="https://example.com/xx.js" autocomplete="off" />
                      </div>
                      <div class="tv-cpo-online-config-editor__actions adm-flex adm-justify-start adm-items-center adm-gap-3">
                        <button type="button" class="btn-add" :class="{ active: canSaveCatConfigEditor }" :disabled="!canSaveCatConfigEditor" @click="confirmCatConfigEditor">
                          确定
                        </button>
                        <button type="button" class="btn-ghost-blue" @click="closeCatConfigEditor">取消</button>
                      </div>
                    </div>
                    <div class="tv-panel tv-cpo-config-table adm-overflow-x-auto">
                      <table class="adm-table-auto adm-w-max adm-max-w-full adm-text-sm adm-text-left adm-text-gray-700">
                        <thead class="table-head">
                          <tr>
                            <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">名称</th>
                            <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">配置地址</th>
                            <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">配置状态</th>
                            <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">最后更新时间</th>
                            <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">更新状态</th>
                            <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">操作</th>
                          </tr>
                        </thead>
                        <tbody>
                          <tr v-if="!catOnlineConfigs.length">
                            <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="6">无数据</td>
                          </tr>
                          <tr v-for="(item, index) in catOnlineConfigs" :key="item.id || `${item.name}-${index}`">
                            <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ item.name || '-' }}</td>
                            <td class="adm-px-3 adm-py-2">{{ item.url || '-' }}</td>
                            <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                              <span class="availability-tag" :class="catConfigStatusTagClass(item.status)">
                                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                  <circle cx="12" cy="12" r="8"></circle>
                                  <circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"></circle>
                                </svg>
                                {{ formatCatConfigStatusText(item.status) }}
                              </span>
                            </td>
                            <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ formatCatConfigTimestamp(item.updateAt) }}</td>
                            <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                              <span class="availability-tag" :class="catConfigUpdateResultTagClass(item.updateResult)">
                                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                  <circle cx="12" cy="12" r="8"></circle>
                                  <circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"></circle>
                                </svg>
                                {{ formatCatConfigUpdateResultText(item.updateResult) }}
                              </span>
                            </td>
                            <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                              <div class="action-group">
                                <button
                                  type="button"
                                  class="action-btn green"
                                  :disabled="catConfigUpdateBusyKey === buildCatConfigActionKey(item, index) || !item.id || catRemoteLoading || catSaving"
                                  @click="triggerCatConfigUpdate(item, index)"
                                >
                                  {{ catConfigUpdateBusyKey === buildCatConfigActionKey(item, index) ? '更新中' : '更新' }}
                                </button>
                                <button type="button" class="action-btn blue" @click="openCatConfigEditorForEdit(index)">修改</button>
                                <button type="button" class="action-btn red" @click="removeCatConfig(index)">删除</button>
                              </div>
                            </td>
                          </tr>
                        </tbody>
                      </table>
                    </div>
                  </div>
                </div>
                <div v-if="showCatSettingsExtras" class="tv-us-acc-item">
                  <button type="button" class="tv-us-acc-head" @click="catPansExpanded = !catPansExpanded">
                    <span>网盘列表</span>
                    <span class="tv-us-acc-icon" :data-open="catPansExpanded ? 'true' : 'false'">
                      <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2">
                        <path stroke-linecap="round" stroke-linejoin="round" d="M6 9l6 6 6-6" />
                      </svg>
                    </span>
                  </button>
                  <div v-if="catPansExpanded" class="tv-us-acc-body">
                    <div class="tv-panel">
                      <div class="tv-row adm-sticky adm-top-0 adm-z-2 adm-mb-2">
                        <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="catPanNameCellStyle">网盘名称</span>
                        <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="catPanHeaderFixedCellStyle">启用</span>
                        <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="catPanHeaderFixedCellStyle">排序</span>
                      </div>
                      <ul class="pan-list adm-space-y-2 adm-text-sm adm-text-gray-700">
                        <li v-if="!catPans.length" class="adm-text-gray-500">暂无网盘数据。</li>
                        <li v-for="(pan, index) in catPans" :key="pan.key || index" class="tv-row">
                          <span :style="catPanNameCellStyle">{{ pan.name || pan.key || '-' }}</span>
                          <span :style="catPanFixedCellStyle">
                            <label class="enable-switch" title="启用">
                              <input v-model="pan.enable" type="checkbox" />
                              <span class="enable-slider"></span>
                            </label>
                          </span>
                          <span :style="catPanFixedCellStyle">
                            <span class="sort-btn-group">
                              <button
                                type="button"
                                class="sort-btn"
                                :disabled="index === 0"
                                @click="moveCatPan(index, 'up')"
                              >
                                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                                  <path d="m18 15-6-6-6 6"/>
                                </svg>
                              </button>
                              <button
                                type="button"
                                class="sort-btn"
                                :disabled="index === catPans.length - 1"
                                @click="moveCatPan(index, 'down')"
                              >
                                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                                  <path d="m6 9 6 6 6-6"/>
                                </svg>
                              </button>
                            </span>
                          </span>
                        </li>
                      </ul>
                    </div>
                    <div class="adm-pt-2 adm-flex adm-justify-start">
                      <button type="button" class="btn-green" :disabled="catPansSaving" @click="saveCatPans">
                        {{ catPansSaving ? '保存中' : '保存网盘列表' }}
                      </button>
                    </div>
                  </div>
                </div>
                <div v-if="showCatSettingsExtras">
                  <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">CatPawRunner 全局代理</label>
                  <input v-model="catForm.proxy" class="tv-field" placeholder="http://127.0.0.1:7890" autocomplete="off" />
                  <div class="adm-text-xs adm-text-gray-500 adm-mt-1">用于 CatPawRunner 请求时的全局代理（留空关闭）。</div>
                </div>
              <div v-if="showCatSettingsExtras" class="adm-space-y-1 adm-pt-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">使用内置网盘解析</div>
                <div>
                  <label class="enable-switch" title="使用内置网盘解析">
                    <input v-model="catForm.panBuiltinResolverEnabled" type="checkbox" />
                    <span class="enable-slider"></span>
                  </label>
                </div>
              </div>
              <div v-if="showCatSettingsExtras" class="adm-space-y-1 adm-pt-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">加速详情获取</div>
                <div>
                  <label class="enable-switch" title="加速详情获取（pan_mock）">
                    <input v-model="catForm.panMockEnabled" type="checkbox" />
                    <span class="enable-slider"></span>
                  </label>
                </div>
                <div class="adm-text-xs adm-text-gray-500">开启后由 MeowFilm 处理网盘数据。</div>
              </div>
              <div v-if="showCatSettingsExtras" class="adm-space-y-1 adm-pt-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">禁用代理透传</div>
                <div>
                  <label class="enable-switch" title="禁用代理透传">
                    <input v-model="catForm.disableProxy" type="checkbox" />
                    <span class="enable-slider"></span>
                  </label>
                </div>
                <div class="adm-text-xs adm-text-gray-500">开启后将禁用 CatPawRunner 的代理透传功能，m3u8 代理除外。</div>
              </div>
              <div v-if="showCatSettingsExtras" class="adm-space-y-2">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">GoProxy API（挂载在 CatPawRunner 同机）</div>
                <input v-model="catForm.goProxyApi" class="tv-field" placeholder="/api 或 https://example.com/" autocomplete="off" />
                <div class="adm-text-xs adm-text-gray-500">用于 m3u8 分片改写指向同机 GoProxy。</div>
              </div>
              <div v-if="showCatSettingsExtras" class="adm-pt-1 adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
                <button type="button" class="btn-green" :disabled="catSyncPanBusy || !normalizedCatApiBase" @click="syncCatPans">
                  {{ catSyncPanBusy ? '同步中' : '同步网盘账号至 CatPawRunner' }}
                </button>
              </div>
              <div class="adm-pt-1 adm-flex adm-items-center adm-gap-2">
                <button type="button" class="btn-green" :disabled="catSaving || !canSaveCatServer" @click="saveCatServer">
                  {{ catSaving ? (catServerAddMode ? '添加中' : '保存中') : (catServerAddMode ? '添加' : '保存') }}
                </button>
              </div>
            </div>
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">GoProxy 设置</div>
            </div>

            <div class="adm-space-y-1">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">GoProxy 启用</div>
              <div>
                <label class="enable-switch" title="GoProxy 启用">
                  <input v-model="goProxyForm.enabled" type="checkbox" />
                  <span class="enable-slider"></span>
                </label>
              </div>
              <div class="adm-text-xs adm-text-gray-500">开启后由客户端将直链注册到 GoProxy。</div>
            </div>

            <div class="adm-space-y-1">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">GoProxy 自动优选</div>
              <div>
                <label class="enable-switch" title="GoProxy 自动优选">
                  <input v-model="goProxyForm.autoSelect" type="checkbox" />
                  <span class="enable-slider"></span>
                </label>
              </div>
              <div class="adm-text-xs adm-text-gray-500">开启后会测速并自动选择最快服务器。</div>
            </div>

            <div class="adm-space-y-2">
              <div class="adm-flex adm-items-center adm-gap-2 adm-mb-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">GoProxy 服务器</div>
                <button type="button" class="btn-green" @click="openGoProxyEditorForCreate">添加</button>
              </div>

              <div class="tv-cpo-config-shell">
                <div v-if="goProxyEditorOpen" class="tv-panel tv-cpo-config-editor adm-mb-3">
                  <div class="user-form-grid">
                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">名称：</span>
                    <input v-model="goProxyEditorForm.name" class="tv-field" placeholder="名称" autocomplete="off" />

                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">显示名称：</span>
                    <input v-model="goProxyEditorForm.displayName" class="tv-field" placeholder="显示名称" autocomplete="off" />

                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">接口地址：</span>
                    <input v-model="goProxyEditorForm.base" class="tv-field" placeholder="http://example.com" autocomplete="off" />
                  </div>
                  <div class="adm-pt-2 adm-space-y-2">
                    <div class="adm-text-sm adm-font-medium adm-text-gray-700">网盘启用</div>
                    <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
                      <label class="adm-flex adm-items-center adm-gap-2 adm-text-sm adm-text-gray-700">
                        <input v-model="goProxyEditorForm.pans.baidu" type="checkbox" />
                        <span>百度</span>
                      </label>
                      <label class="adm-flex adm-items-center adm-gap-2 adm-text-sm adm-text-gray-700">
                        <input v-model="goProxyEditorForm.pans.quark" type="checkbox" />
                        <span>夸克</span>
                      </label>
                    </div>
                  </div>
                  <div class="adm-flex adm-justify-start adm-items-center adm-gap-3 adm-mt-3">
                    <button type="button" class="btn-green" :disabled="!canSaveGoProxyEditor" @click="confirmGoProxyEditor">
                      {{ goProxyEditorMode === 'edit' ? '保存' : '添加' }}
                    </button>
                    <button type="button" class="btn-ghost-blue" @click="closeGoProxyEditor">取消</button>
                  </div>
                </div>

                <div class="tv-panel tv-cpo-config-table adm-overflow-x-auto">
                  <table class="adm-table-auto adm-w-max adm-max-w-full adm-text-sm adm-text-left adm-text-gray-700">
                    <thead class="table-head">
                      <tr>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">名称</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">显示名称</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">接口地址</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">版本</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">状态</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">网盘启用</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">操作</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr v-if="!goProxyServers.length">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">无数据</td>
                      </tr>
                      <tr v-for="server in goProxyServers" :key="server.base">
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ server.name || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ server.displayName || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ displayGoProxyBaseHost(server.base) || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ formatGoProxyVersion(server.base) }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                          <span class="availability-tag" :class="goProxyProbeTagClass(getGoProxyProbe(server.base).state)">
                            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                              <circle cx="12" cy="12" r="8"></circle>
                              <circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"></circle>
                            </svg>
                            {{ goProxyProbeTextFor(getGoProxyProbe(server.base).state) }}
                          </span>
                        </td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                          <div class="go-proxy-pan-switches">
                            <div class="go-proxy-pan-switch">
                              <span>百度</span>
                              <label class="enable-switch" title="百度">
                                <input
                                  :checked="normalizeGoProxyPanMap(server.pans).baidu"
                                  type="checkbox"
                                  @change="setGoProxyPanEnabled(server.base, 'baidu', $event.target.checked)"
                                />
                                <span class="enable-slider"></span>
                              </label>
                            </div>
                            <div class="go-proxy-pan-switch">
                              <span>夸克</span>
                              <label class="enable-switch" title="夸克">
                                <input
                                  :checked="normalizeGoProxyPanMap(server.pans).quark"
                                  type="checkbox"
                                  @change="setGoProxyPanEnabled(server.base, 'quark', $event.target.checked)"
                                />
                                <span class="enable-slider"></span>
                              </label>
                            </div>
                          </div>
                        </td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                          <div class="action-group">
                            <button type="button" class="action-btn blue" @click="openGoProxyEditorForEdit(server)">修改</button>
                            <button type="button" class="action-btn red" @click="removeGoProxyServer(server.base)">删除</button>
                          </div>
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>

            <div class="adm-pt-1 adm-flex adm-justify-start">
              <button type="button" class="btn-green" :disabled="goProxySaving" @click="saveGoProxySettings">
                {{ goProxySaving ? '保存中' : '保存' }}
              </button>
            </div>
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">边缘函数设置</div>
            </div>

            <div class="adm-space-y-1">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">边缘函数启用</div>
              <div>
                <label class="enable-switch" title="边缘函数">
                  <input v-model="relayForm.enabled" type="checkbox" />
                  <span class="enable-slider"></span>
                </label>
              </div>
            </div>

            <div class="adm-space-y-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">auth</div>
              <input v-model="relayForm.auth" class="tv-field" placeholder="auth" autocomplete="off" />
              <div class="adm-text-xs adm-text-gray-500">用于函数服务器访问 MeowFilm 接口。</div>
            </div>

            <div class="adm-space-y-2">
              <div class="adm-flex adm-items-center adm-gap-2 adm-mb-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">函数服务器</div>
                <button type="button" class="btn-green" @click="openRelayEditorForCreate">添加</button>
              </div>

              <div class="tv-cpo-config-shell">
                <div v-if="relayEditorOpen" class="tv-panel tv-cpo-config-editor adm-mb-3">
                  <div class="user-form-grid">
                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">名称：</span>
                    <input v-model="relayEditorForm.name" class="tv-field" placeholder="名称" autocomplete="off" />

                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">显示名称：</span>
                    <input v-model="relayEditorForm.displayName" class="tv-field" placeholder="显示名称" autocomplete="off" />

                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">接口地址：</span>
                    <input v-model="relayEditorForm.base" class="tv-field" placeholder="http://example.com" autocomplete="off" />

                    <span class="adm-text-sm adm-font-medium adm-text-gray-700 user-form-grid__label">密钥：</span>
                    <input v-model="relayEditorForm.secret" class="tv-field" placeholder="密钥" autocomplete="off" />
                  </div>
                  <div class="adm-flex adm-justify-start adm-items-center adm-gap-3 adm-mt-3">
                    <button type="button" class="btn-green" :disabled="!canSaveRelayEditor" @click="confirmRelayEditor">
                      {{ relayEditorMode === 'edit' ? '保存' : '添加' }}
                    </button>
                    <button type="button" class="btn-ghost-blue" @click="closeRelayEditor">取消</button>
                  </div>
                </div>

                <div class="tv-panel tv-cpo-config-table adm-overflow-x-auto">
                  <table class="adm-table-auto adm-w-max adm-max-w-full adm-text-sm adm-text-left adm-text-gray-700">
                    <thead class="table-head">
                      <tr>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">名称</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">显示名称</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">接口地址</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">版本</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">状态</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">密钥</th>
                        <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">操作</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr v-if="!relayServers.length">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">无数据</td>
                      </tr>
                      <tr v-for="server in relayServers" :key="server.base">
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ server.name || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ server.displayName || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ displayGoProxyBaseHost(server.base) || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ formatRelayVersion(server.base) }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                          <span class="availability-tag" :class="goProxyProbeTagClass(getRelayProbe(server.base).state)">
                            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                              <circle cx="12" cy="12" r="8"></circle>
                              <circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"></circle>
                            </svg>
                            {{ goProxyProbeTextFor(getRelayProbe(server.base).state) }}
                          </span>
                        </td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">{{ server.secret ? '已设置' : '未设置' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                          <div class="action-group">
                            <button type="button" class="action-btn blue" @click="openRelayEditorForEdit(server)">修改</button>
                            <button type="button" class="action-btn red" @click="removeRelayServer(server.base)">删除</button>
                          </div>
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>

            <div class="adm-pt-1 adm-flex adm-justify-start">
              <button type="button" class="btn-green" :disabled="relaySaving" @click="saveRelaySettings">
                {{ relaySaving ? '保存中' : '保存' }}
              </button>
            </div>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'video'"
          id="dashboardVideo"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <FilmIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            视频源管理
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-justify-start">
              <div class="adm-flex adm-flex-col adm-gap-2">
                <div class="adm-flex adm-items-center adm-gap-2">
                  <button type="button" class="btn-green" :disabled="videoImporting" @click="importVideoSourcesFromCatpawrunner">
                    {{ videoImporting ? '导入中' : '从 CatPawRunner 导入站源' }}
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div class="tv-us-acc-item">
            <button type="button" class="tv-us-acc-head" @click="videoSitesExpanded = !videoSitesExpanded">
              <span>站点列表</span>
              <span class="tv-us-acc-icon" :data-open="videoSitesExpanded ? 'true' : 'false'">
                <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2">
                  <path stroke-linecap="round" stroke-linejoin="round" d="M6 9l6 6 6-6" />
                </svg>
              </span>
            </button>
            <div v-if="videoSitesExpanded" class="tv-us-acc-body">
              <div class="adm-flex adm-items-center adm-gap-2 adm-mb-2">
                <button type="button" class="btn-ghost-blue" @click="pickVideoSourceImportFile">导入站点</button>
                <button type="button" class="btn-ghost-blue" @click="exportVideoSourcesToJson">导出站点</button>
                <input
                  ref="videoSourceImportFileRef"
                  type="file"
                  accept="application/json,.json"
                  class="hidden"
                  @change="importVideoSourcesFromJson"
                />
              </div>
              <div class="adm-flex adm-items-center adm-justify-between adm-gap-3 adm-mb-2 adm-flex-wrap">
                <div class="adm-flex adm-items-center adm-gap-2 adm-flex-wrap">
                  <button type="button" class="btn-pill-blue btn-pill-small" @click="resetVideoSourceOrderFromCatpawrunner">重置排序</button>
                  <div v-if="hasSelectedVideoSources" class="adm-flex adm-items-center adm-gap-2 adm-flex-wrap">
                    <button type="button" class="btn-pill-blue btn-pill-small" @click="bulkCheckAndDisableVideoSources">检测并禁用失效源</button>
                    <button type="button" class="btn-pill-green btn-pill-small" @click="bulkToggleVideoSourceEnabled(true)">批量启用</button>
                    <button type="button" class="btn-pill-red btn-pill-small" @click="bulkToggleVideoSourceEnabled(false)">批量禁用</button>
                  </div>
                </div>
              </div>
              <div class="adm-max-h-60vh adm-overflow-y-auto tv-panel">
                <div class="video-source-header tv-row adm-sticky adm-top-0 adm-z-2 adm-mb-2">
                  <input
                    type="checkbox"
                    class="video-source-checkbox"
                    :checked="allVideoSourceSelected"
                    @change="toggleAllVideoSources($event.target.checked)"
                  />
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceNameCellStyle">站源名称</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceApiCellStyle">路由/API</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceFixed96CellStyle">站点可用性</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceFixed72CellStyle">是否启用</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceFixed72CellStyle">首页显示</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceFixed72CellStyle">搜索启用</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceFixed96CellStyle">聚合图片显示</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceFixed72CellStyle">排序</span>
                  <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="videoSourceErrorCellStyle">错误信息</span>
                </div>
                <ul class="pan-list adm-space-y-2 adm-text-sm adm-text-gray-700">
                  <li v-if="!videoSourceSites.length" class="adm-text-gray-500">暂无站源，解析完成后会在这里展示。</li>
                  <li v-for="(site, index) in videoSourceSites" :key="site.key || index" class="tv-row">
                    <input
                      type="checkbox"
                      class="video-source-checkbox"
                      :checked="selectedVideoSourceKeys.includes(site.key)"
                      @change="toggleVideoSourceSelected(site.key, $event.target.checked)"
                    />
                    <span class="adm-text-sm adm-font-medium adm-text-gray-700" :style="videoSourceNameCellStyle">{{ site.name || site.key || '-' }}</span>
                    <span class="adm-text-xs adm-text-gray-500" :style="videoSourceApiCellStyle">{{ formatVideoSourceApi(site.api) || '-' }}</span>
                    <span :style="videoSourceFixed96CellStyle">
                      <span class="availability-tag" :class="availabilityTagClass(site.availability)">
                        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                          <circle cx="12" cy="12" r="8"></circle>
                          <circle cx="12" cy="12" r="2" fill="currentColor" stroke="none"></circle>
                        </svg>
                        {{ availabilityText(site.availability) }}
                      </span>
                    </span>
                    <span :style="videoSourceFixed72CellStyle">
                      <label class="enable-switch" title="是否启用">
                        <input :checked="site.enabled !== false" type="checkbox" @change="toggleVideoSourceEnabled(site, $event.target.checked)" />
                        <span class="enable-slider"></span>
                      </label>
                    </span>
                    <span :style="videoSourceFixed72CellStyle">
                      <label class="enable-switch" title="首页显示">
                        <input :checked="site.home !== false" type="checkbox" :disabled="site.enabled === false" @change="toggleVideoSourceHome(site, $event.target.checked)" />
                        <span class="enable-slider"></span>
                      </label>
                    </span>
                    <span :style="videoSourceFixed72CellStyle">
                      <label class="enable-switch" title="搜索启用">
                        <input :checked="site.search !== false" type="checkbox" :disabled="site.enabled === false" @change="toggleVideoSourceSearch(site, $event.target.checked)" />
                        <span class="enable-slider"></span>
                      </label>
                    </span>
                    <span :style="videoSourceFixed96CellStyle">
                      <label class="enable-switch" title="聚合图片显示">
                        <input :checked="videoSourceCoverSite === site.key" type="checkbox" :disabled="site.enabled === false" @change="toggleVideoSourceCover(site, $event.target.checked)" />
                        <span class="enable-slider"></span>
                      </label>
                    </span>
                    <span :style="videoSourceFixed72CellStyle">
                      <span class="sort-btn-group">
                        <button type="button" class="sort-btn" :disabled="index === 0" @click="moveVideoSourceAndSave(index, 'up')">
                          <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m18 15-6-6-6 6"/></svg>
                        </button>
                        <button type="button" class="sort-btn" :disabled="index === videoSourceSites.length - 1" @click="moveVideoSourceAndSave(index, 'down')">
                          <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6"/></svg>
                        </button>
                      </span>
                    </span>
                    <span class="adm-text-xs adm-text-gray-500" :style="videoSourceErrorCellStyle">{{ site.error || '' }}</span>
                  </li>
                </ul>
              </div>
            </div>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'magic'"
          id="dashboardMagic"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <WandIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            魔法匹配设置
          </div>

          <div class="dashboard-card adm-space-y-3">
            <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">列表净化设置</div>
              <button
                v-if="!magicEpisodeDefaultsConfirming"
                type="button"
                class="btn-ghost-red"
                :disabled="magicSaving"
                @click="magicEpisodeDefaultsConfirming = true"
              >
                恢复默认规则
              </button>
              <button
                v-else
                type="button"
                class="btn-ghost-red"
                :disabled="magicSaving"
                @click="restoreMagicEpisodeDefaults"
              >
                确定
              </button>
              <button
                v-if="magicEpisodeDefaultsConfirming"
                type="button"
                class="btn-ghost-blue"
                :disabled="magicSaving"
                @click="magicEpisodeDefaultsConfirming = false"
              >
                取消
              </button>
            </div>

            <div class="adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-2">剧集列表净化规则</div>
            <div class="adm-flex adm-items-center adm-gap-2 magic-rule-add-row">
              <div class="adm-min-w-0" style="flex: 0 0 50%; max-width: 50%;">
                <input
                  v-model="magicEpisodeCleanRuleInput"
                  class="tv-field adm-min-w-0"
                  placeholder="\[.*?\]|\(.*?\)|更新至\d+集"
                />
              </div>
              <button type="button" class="btn-green" :disabled="magicSaving" @click="addMagicEpisodeCleanRule">添加</button>
            </div>
            <ul class="magic-rule-list magic-rule-list--single">
              <li v-if="!magicEpisodeCleanRegexRules.length" class="adm-text-gray-500">无数据</li>
              <li v-for="(rule, index) in magicEpisodeCleanRegexRules" :key="`episode-clean-${index}`" class="magic-rule-row">
                <div class="magic-rule-shell">
                  <div class="magic-rule-index">{{ index + 1 }}.</div>
                  <div class="magic-rule-single">
                    <input v-model="magicEpisodeCleanRegexRules[index]" class="tv-field adm-min-w-0" />
                  </div>
                  <div class="action-group">
                    <button type="button" class="action-btn green" :disabled="magicSaving" @click="saveMagicBlock">保存</button>
                    <button type="button" class="action-btn red" :disabled="magicSaving" @click="removeMagicEpisodeCleanRule(index)">删除</button>
                  </div>
                </div>
                <div v-if="magicEpisodeCleanRulePreviews[index]" class="magic-rule-preview">
                  {{ magicEpisodeCleanRulePreviews[index] }}
                </div>
              </li>
            </ul>

            <div class="adm-pt-3 adm-space-y-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">集数匹配规则</div>
            </div>
            <div class="adm-flex adm-items-center adm-gap-2 magic-rule-add-row">
              <div class="magic-rule-pair magic-rule-pair--add">
                <input
                  v-model="magicEpisodeRulePatternInput"
                  class="tv-field adm-min-w-0"
                  style="flex: 5 1 0;"
                  placeholder=".*?([Ss]\d{1,2})?(?:[第EePpXx\.\-_( ]{1,2}|^)(\d{1,3})(?!\d).*?\.(mp4|mkv)"
                />
                <input
                  v-model="magicEpisodeRuleReplaceInput"
                  class="tv-field adm-min-w-0"
                  style="flex: 1 1 0;"
                  placeholder="$1E$2.$3"
                />
              </div>
              <button type="button" class="btn-green" :disabled="magicSaving" @click="addMagicEpisodeRule">添加</button>
            </div>
            <ul class="magic-rule-list magic-rule-list--pair">
              <li v-if="!magicEpisodeRules.length" class="adm-text-gray-500">无数据</li>
              <li v-for="(rule, index) in magicEpisodeRules" :key="`episode-rule-${index}`" class="magic-rule-row">
                <div class="magic-rule-shell">
                  <div class="magic-rule-index">{{ index + 1 }}.</div>
                  <div class="magic-rule-pair">
                    <input v-model="magicEpisodeRules[index].pattern" class="tv-field adm-min-w-0" style="flex: 5 1 0;" />
                    <input v-model="magicEpisodeRules[index].replace" class="tv-field adm-min-w-0" style="flex: 1 1 0;" />
                  </div>
                  <div class="action-group">
                    <button type="button" class="action-btn green" :disabled="magicSaving" @click="saveMagicBlock">保存</button>
                    <button type="button" class="action-btn red" :disabled="magicSaving" @click="removeMagicEpisodeRule(index)">删除</button>
                  </div>
                </div>
              </li>
            </ul>

            <div class="adm-pt-2 adm-space-y-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">规则测试</div>
              <div class="adm-flex adm-items-center adm-gap-2">
                <div class="adm-min-w-0" style="flex: 0 0 50%; max-width: 50%;">
                  <input v-model="magicEpisodeRuleTestInput" class="tv-field adm-min-w-0" placeholder="仙逆.S01E006.mkv" />
                </div>
                <button type="button" class="btn-green" @click="runMagicEpisodeRuleTest">测试</button>
              </div>
              <div v-if="magicEpisodeRuleTestOutput.text" class="magic-test-output">
                {{ magicEpisodeRuleTestOutput.text }}
              </div>
            </div>
          </div>

          <div class="dashboard-card adm-space-y-3">
            <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">电影匹配设置</div>
              <button
                v-if="!magicMovieDefaultsConfirming"
                type="button"
                class="btn-ghost-red"
                :disabled="magicSaving"
                @click="magicMovieDefaultsConfirming = true"
              >
                恢复默认规则
              </button>
              <button
                v-else
                type="button"
                class="btn-ghost-red"
                :disabled="magicSaving"
                @click="restoreMagicMovieDefaults"
              >
                确定
              </button>
              <button
                v-if="magicMovieDefaultsConfirming"
                type="button"
                class="btn-ghost-blue"
                :disabled="magicSaving"
                @click="magicMovieDefaultsConfirming = false"
              >
                取消
              </button>
            </div>

            <div class="adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-2">电影匹配规则</div>
            <div class="adm-flex adm-items-center adm-gap-2 magic-rule-add-row">
              <div class="magic-rule-pair magic-rule-pair--add">
                <input v-model="magicMovieRulePatternInput" class="tv-field adm-min-w-0" placeholder="^(?!.*S\d{1,2}\s*E\d{1,3}).*?\.(mkv|mp4)$" />
                <input v-model="magicMovieRuleReplaceInput" class="tv-field adm-min-w-0" placeholder="replace（可空）" />
              </div>
              <button type="button" class="btn-green" :disabled="magicSaving" @click="addMagicMovieRule">添加</button>
            </div>
            <ul class="magic-rule-list magic-rule-list--pair">
              <li v-if="!magicMovieRules.length" class="adm-text-gray-500">无数据</li>
              <li v-for="(rule, index) in magicMovieRules" :key="`movie-rule-${index}`" class="magic-rule-row">
                <div class="magic-rule-shell">
                  <div class="magic-rule-index">{{ index + 1 }}.</div>
                  <div class="magic-rule-pair">
                    <input v-model="magicMovieRules[index].pattern" class="tv-field adm-min-w-0" />
                    <input v-model="magicMovieRules[index].replace" class="tv-field adm-min-w-0" />
                  </div>
                  <div class="action-group">
                    <button type="button" class="action-btn green" :disabled="magicSaving" @click="saveMagicBlock">保存</button>
                    <button type="button" class="action-btn red" :disabled="magicSaving" @click="removeMagicMovieRule(index)">删除</button>
                  </div>
                </div>
              </li>
            </ul>

            <div class="adm-pt-2 adm-space-y-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">规则测试</div>
              <div class="adm-flex adm-items-center adm-gap-2">
                <div class="adm-min-w-0" style="flex: 0 0 50%; max-width: 50%;">
                  <input v-model="magicMovieRuleTestInput" class="tv-field adm-min-w-0" placeholder="Hamnet (2025) (1080p WEB-DL).mkv" />
                </div>
                <button type="button" class="btn-green" @click="runMagicMovieRuleTest">测试</button>
              </div>
              <div v-if="magicMovieRuleTestOutput.text" class="magic-test-output">
                {{ magicMovieRuleTestOutput.text }}
              </div>
            </div>
          </div>

          <div class="dashboard-card adm-space-y-3">
            <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">搜索列表净化设置</div>
              <button
                v-if="!magicAggregateDefaultsConfirming"
                type="button"
                class="btn-ghost-red"
                :disabled="magicSaving"
                @click="magicAggregateDefaultsConfirming = true"
              >
                恢复默认规则
              </button>
              <button
                v-else
                type="button"
                class="btn-ghost-red"
                :disabled="magicSaving"
                @click="restoreMagicAggregateDefaults"
              >
                确定
              </button>
              <button
                v-if="magicAggregateDefaultsConfirming"
                type="button"
                class="btn-ghost-blue"
                :disabled="magicSaving"
                @click="magicAggregateDefaultsConfirming = false"
              >
                取消
              </button>
            </div>

            <div class="adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-2">搜索列表净化规则</div>
            <div class="adm-flex adm-items-center adm-gap-2 magic-rule-add-row">
              <div class="adm-min-w-0" style="flex: 0 0 50%; max-width: 50%;">
                <input v-model="magicAggregateRuleInput" class="tv-field adm-min-w-0" placeholder="\(\d{4}\)|\[.*?\]|更新至\d+集" />
              </div>
              <button type="button" class="btn-green" :disabled="magicSaving" @click="addMagicAggregateRule">添加</button>
            </div>
            <ul class="magic-rule-list magic-rule-list--single">
              <li v-if="!magicAggregateRegexRules.length" class="adm-text-gray-500">无数据</li>
              <li v-for="(rule, index) in magicAggregateRegexRules" :key="`aggregate-rule-${index}`" class="magic-rule-row">
                <div class="magic-rule-shell">
                  <div class="magic-rule-index">{{ index + 1 }}.</div>
                  <div class="magic-rule-single">
                    <input v-model="magicAggregateRegexRules[index]" class="tv-field adm-min-w-0" />
                  </div>
                  <div class="action-group">
                    <button type="button" class="action-btn green" :disabled="magicSaving" @click="saveMagicBlock">保存</button>
                    <button type="button" class="action-btn red" :disabled="magicSaving" @click="removeMagicAggregateRule(index)">删除</button>
                  </div>
                </div>
                <div v-if="magicAggregateRulePreviews[index]" class="magic-rule-preview">
                  {{ magicAggregateRulePreviews[index] }}
                </div>
              </li>
            </ul>

            <div class="adm-pt-2 adm-space-y-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">规则测试</div>
              <div class="adm-flex adm-flex-col adm-gap-2">
                <div class="adm-flex adm-items-center adm-gap-2">
                  <div class="adm-min-w-0" style="flex: 0 0 50%; max-width: 50%;">
                    <input v-model="magicAggregateRuleTestQueryInput" class="tv-field adm-min-w-0" placeholder="搜索关键字（可选）" />
                  </div>
                </div>
                <div class="adm-flex adm-items-center adm-gap-2">
                  <div class="adm-min-w-0" style="flex: 0 0 50%; max-width: 50%;">
                    <input v-model="magicAggregateRuleTestInput" class="tv-field adm-min-w-0" placeholder="搜索结果标题（如：仙逆 年番 更新至123集 (2023)）" />
                  </div>
                  <button type="button" class="btn-green" @click="runMagicAggregateRuleTest">测试</button>
                </div>
              </div>
              <div v-if="magicAggregateRuleTestOutput.text" class="magic-test-output">
                {{ magicAggregateRuleTestOutput.text }}
              </div>
            </div>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'smart'"
          id="dashboardSmart"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <SparklesIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            智能播放设置
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">偏好设置</div>
              <button
                v-if="!smartPanDefaultsConfirming"
                type="button"
                class="btn-ghost-red"
                :disabled="smartSaving"
                @click="smartPanDefaultsConfirming = true"
              >
                恢复默认规则
              </button>
              <button
                v-else
                type="button"
                class="btn-ghost-red"
                :disabled="smartSaving"
                @click="restoreSmartPanDefaults"
              >
                确定
              </button>
              <button
                v-if="smartPanDefaultsConfirming"
                type="button"
                class="btn-ghost-blue"
                :disabled="smartSaving"
                @click="smartPanDefaultsConfirming = false"
              >
                取消
              </button>
            </div>

            <div class="adm-space-y-3">
              <div class="adm-space-y-1">
                <div class="adm-text-sm adm-font-medium adm-text-gray-700">优先片源规则</div>
                <div class="tv-panel">
                  <div class="tv-row adm-sticky adm-top-0 adm-z-2 adm-mb-2">
                    <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="catPanNameCellStyle">片源名称</span>
                    <span class="adm-text-sm adm-font-medium adm-text-gray-600" :style="catPanHeaderFixedCellStyle">排序</span>
                  </div>
                  <ul class="pan-list adm-space-y-2 adm-text-sm adm-text-gray-700">
                    <li
                      v-for="(rule, index) in smartSourceRuleRows"
                      :key="rule.key"
                      class="tv-row"
                    >
                      <span :style="catPanNameCellStyle">{{ rule.label }}</span>
                      <span :style="catPanFixedCellStyle">
                        <span class="sort-btn-group">
                          <button
                            type="button"
                            class="sort-btn"
                            :disabled="smartSaving || index === 0"
                            @click="moveSmartSourceRuleRow(index, 'up')"
                          >
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                              <path d="m18 15-6-6-6 6"/>
                            </svg>
                          </button>
                          <button
                            type="button"
                            class="sort-btn"
                            :disabled="smartSaving || index === smartSourceRuleRows.length - 1"
                            @click="moveSmartSourceRuleRow(index, 'down')"
                          >
                            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                              <path d="m6 9 6 6 6-6"/>
                            </svg>
                          </button>
                        </span>
                      </span>
                    </li>
                  </ul>
                </div>
              </div>
            </div>

            <div class="adm-pt-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">关键字匹配规则</div>
            </div>
            <div>
              <input
                v-model="smartSourcePriorityTokensInput"
                class="tv-field"
                :disabled="smartSaving"
                placeholder="DDP,H.265"
              />
            </div>

            <div class="adm-pt-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">网盘匹配规则</div>
            </div>
            <div>
              <input
                v-model="smartPanMatchTokensInput"
                class="tv-field"
                :disabled="smartSaving"
                placeholder="移动,天翼,夸克,uc,百度,115"
              />
            </div>
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">站点净化设置</div>
              <button
                v-if="!smartSiteCleanDefaultsConfirming"
                type="button"
                class="btn-ghost-red"
                :disabled="smartSaving"
                @click="smartSiteCleanDefaultsConfirming = true"
              >
                恢复默认规则
              </button>
              <button
                v-else
                type="button"
                class="btn-ghost-red"
                :disabled="smartSaving"
                @click="restoreSmartSiteCleanDefaults"
              >
                确定
              </button>
              <button
                v-if="smartSiteCleanDefaultsConfirming"
                type="button"
                class="btn-ghost-blue"
                :disabled="smartSaving"
                @click="smartSiteCleanDefaultsConfirming = false"
              >
                取消
              </button>
            </div>
            <div class="adm-text-xs adm-text-gray-500">
              智能播放不会在匹配到的站点进行匹配片源
            </div>
            <div class="adm-pt-2">
              <div class="adm-text-sm adm-font-medium adm-text-gray-700">关键字匹配规则</div>
            </div>
            <div>
              <input
                v-model="smartSiteCleanKeywordsInput"
                class="tv-field"
                :disabled="smartSaving"
                placeholder="直播,体育,短剧,听书,舞曲,哔哩"
              />
            </div>
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">匹配禁用管理</div>
            </div>
            <div class="adm-text-xs adm-text-gray-500">
              在搜索页原始列表中右击站点卡片可添加禁用项（仅影响智能播放匹配）
            </div>
            <div class="tv-panel adm-overflow-hidden">
              <div class="adm-flex adm-items-center adm-divide-x adm-divide-gray-100">
                <div class="adm-w-64 adm-flex-shrink-0 adm-px-4 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700">禁用数据</div>
                <div class="adm-flex-1 adm-min-w-0 adm-overflow-auto">
                  <div class="smart-match-head-grid">
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">预览</div>
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">站源</div>
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">路由/API</div>
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">来源</div>
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">网盘标识</div>
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">详情ID</div>
                    <div class="adm-px-3 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-whitespace-nowrap">操作</div>
                  </div>
                </div>
              </div>

              <div class="adm-flex adm-divide-x adm-divide-gray-100">
                <div class="adm-w-64 adm-flex-shrink-0 adm-max-h-60vh adm-overflow-y-auto">
                  <ul class="adm-p-3 adm-space-y-2">
                    <li
                      v-if="smartMatchBlockKeywordsLoading"
                      class="adm-px-4 adm-py-3 adm-text-gray-500 adm-text-sm"
                    >
                      加载中...
                    </li>
                    <li
                      v-else-if="!smartMatchBlockKeywords.length"
                      class="adm-px-4 adm-py-3 adm-text-gray-500 adm-text-sm"
                    >
                      无数据
                    </li>
                    <li
                      v-for="row in smartMatchBlockKeywords"
                      :key="row.keyword"
                      class="smart-match-keyword-chip adm-px-4 adm-py-2 adm-rounded-full adm-border adm-flex adm-items-center adm-gap-2 adm-transition-colors adm-max-w-full"
                      :data-active="smartMatchBlockSelectedKeyword === row.keyword"
                      :style="smartMatchBlockSelectedKeyword === row.keyword
                        ? 'background: rgba(34,197,94,.10); border-color: rgba(34,197,94,.30);'
                        : 'border-color: #e5e7eb; cursor: pointer;'"
                      @click="toggleSmartMatchBlockKeyword(row.keyword)"
                    >
                      <div class="smart-match-keyword-main">
                        <div class="adm-text-sm adm-text-gray-700 smart-match-keyword-name">{{ row.keyword }}</div>
                        <div class="smart-match-keyword-count">{{ row.count || 0 }}</div>
                      </div>
                      <div class="adm-flex-shrink-0">
                        <button
                          type="button"
                          class="action-btn red"
                          :disabled="smartMatchBlockKeywordsLoading || smartMatchBlockItemsLoading"
                          @click.stop="removeSmartMatchBlockKeyword(row.keyword)"
                        >
                          删除
                        </button>
                      </div>
                    </li>
                  </ul>
                </div>

                <div class="adm-flex-1 adm-min-w-0 adm-max-h-60vh adm-overflow-auto tv-cpo-config-table">
                  <table class="smart-match-table adm-text-sm adm-text-left adm-text-gray-700">
                    <colgroup>
                      <col style="width:72px;" />
                      <col style="width:160px;" />
                      <col style="width:280px;" />
                      <col style="width:96px;" />
                      <col style="width:160px;" />
                      <col style="width:220px;" />
                      <col style="width:88px;" />
                    </colgroup>
                    <tbody>
                      <tr v-if="smartMatchBlockItemsLoading">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">加载中...</td>
                      </tr>
                      <tr v-else-if="!smartMatchBlockSelectedKeyword">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">请选择关键字</td>
                      </tr>
                      <tr v-else-if="!smartMatchBlockItems.length">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">无数据</td>
                      </tr>
                      <tr v-for="item in smartMatchBlockItems" :key="`${item.keyword}@@${item.siteKey}@@${item.siteDetail}@@${item.panFlag || ''}@@${item.source || ''}`">
                        <td class="adm-px-3 adm-py-2">
                          <img
                            v-if="item.poster"
                            :src="item.poster"
                            alt=""
                            loading="lazy"
                            decoding="async"
                            referrerpolicy="no-referrer"
                            style="width:40px;height:56px;object-fit:cover;border-radius:6px;background:#e5e7eb;display:block;"
                          />
                          <div
                            v-else
                            style="width:40px;height:56px;border-radius:6px;background:#e5e7eb;"
                          ></div>
                        </td>
                        <td class="adm-px-3 adm-py-2">{{ item.siteName || item.siteKey || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-text-xs" style="font-family:ui-monospace,SFMono-Regular,Menlo,monospace;">{{ item.spiderApi || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-text-xs">{{ item.source || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-text-xs">{{ item.panFlag || '-' }}</td>
                        <td class="adm-px-3 adm-py-2 adm-text-xs" style="font-family:ui-monospace,SFMono-Regular,Menlo,monospace;">{{ item.siteDetail || '-' }}</td>
                        <td class="adm-px-3 adm-py-2">
                          <button
                            type="button"
                            class="action-btn red"
                            :disabled="smartMatchBlockItemsLoading"
                            @click="removeSmartMatchBlockItem(item)"
                          >
                            删除
                          </button>
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">Manual 匹配源管理</div>
            </div>
            <div class="tv-panel adm-overflow-hidden">
              <div class="adm-flex adm-divide-x adm-divide-gray-100">
                <div class="adm-w-64 adm-flex-shrink-0 adm-px-4 adm-py-3 adm-text-sm adm-font-medium adm-text-gray-700 adm-flex adm-items-center adm-justify-between adm-gap-2">
                  <span>TMDB 数据</span>
                  <button type="button" class="btn-green adm-rounded-full" @click="openManualTmdbDialog">添加</button>
                </div>
                <div class="adm-flex-1 adm-min-w-0 adm-overflow-auto">
                  <table class="smart-manual-table smart-manual-head-table adm-text-sm adm-text-left adm-text-gray-700" :style="manualColumnLayoutStyle">
                    <colgroup>
                      <col style="width:160px;" />
                      <col :style="{ width: manualDetailColWidth }" />
                      <col :style="{ width: manualPanFlagColWidth }" />
                      <col style="width:120px;" />
                      <col style="width:96px;" />
                      <col style="width:80px;" />
                      <col style="width:120px;" />
                      <col style="width:96px;" />
                    </colgroup>
                    <thead>
                      <tr>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">站源</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">详情ID</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">panflag</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">季度补全</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">自动失效</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">状态</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-left">操作</th>
                        <th class="adm-px-3 adm-py-3 adm-font-medium adm-whitespace-nowrap adm-text-right">
                          <button
                            v-if="manualTmdbSelectedReady"
                            type="button"
                            class="btn-green adm-rounded-full"
                            @click="toggleManualItemEditor"
                          >
                            {{ manualItemEditorOpen ? '取消' : '添加' }}
                          </button>
                        </th>
                      </tr>
                    </thead>
                  </table>
                </div>
              </div>

              <div class="adm-flex adm-divide-x adm-divide-gray-100">
                <div class="adm-w-64 adm-flex-shrink-0 adm-max-h-60vh adm-overflow-y-auto">
                  <ul class="adm-p-3 adm-space-y-2">
                    <li v-if="manualTmdbItemsLoading" class="adm-px-4 adm-py-3 adm-text-gray-500 adm-text-sm">加载中...</li>
                    <li v-else-if="!manualTmdbItems.length" class="adm-px-4 adm-py-3 adm-text-gray-500 adm-text-sm">无数据</li>
                    <li
                      v-for="item in manualTmdbItems"
                      :key="`manual-tmdb-${item.tmdbType}-${item.tmdbId}`"
                      class="smart-match-keyword-chip adm-px-4 adm-py-2 adm-rounded-full adm-border adm-flex adm-items-center adm-gap-2 adm-transition-colors adm-max-w-full"
                      :data-active="isManualTmdbItemSelected(item)"
                      :style="isManualTmdbItemSelected(item)
                        ? 'background: rgba(34,197,94,.10); border-color: rgba(34,197,94,.30);'
                        : 'border-color: #e5e7eb; cursor: pointer;'"
                      @click="toggleManualTmdbItem(item)"
                    >
                      <div class="smart-match-keyword-main">
                        <div class="adm-text-sm adm-text-gray-700 smart-match-keyword-name">{{ item.title }}({{ item.tmdbType }}:{{ item.tmdbId }})</div>
                        <div class="smart-match-keyword-count">{{ item.count || 0 }}</div>
                      </div>
                      <div class="adm-flex-shrink-0">
                        <button
                          type="button"
                          class="action-btn red"
                          :disabled="manualTmdbItemsLoading || manualTmdbDeletingKey === `${item.tmdbType}:${item.tmdbId}`"
                          @click.stop="removeManualTmdbItem(item)"
                        >
                          删除
                        </button>
                      </div>
                    </li>
                  </ul>
                </div>

                <div class="adm-flex-1 adm-min-w-0 adm-max-h-60vh adm-overflow-auto tv-cpo-config-table">
                  <table class="smart-manual-table adm-text-sm adm-text-left adm-text-gray-700" :style="manualColumnLayoutStyle">
                    <colgroup>
                      <col style="width:160px;" />
                      <col :style="{ width: manualDetailColWidth }" />
                      <col :style="{ width: manualPanFlagColWidth }" />
                      <col style="width:120px;" />
                      <col style="width:96px;" />
                      <col style="width:80px;" />
                      <col style="width:120px;" />
                      <col style="width:96px;" />
                    </colgroup>
                    <tbody>
                      <tr v-if="!manualTmdbSelectedReady">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="8">请选择 TMDB 数据</td>
                      </tr>
                      <tr v-else-if="manualItemRowsLoading">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="8">加载中...</td>
                      </tr>
                      <tr v-else-if="manualItemEditorOpen">
                        <td class="adm-px-3 adm-py-2"></td>
                        <td class="adm-px-3 adm-py-2"></td>
                        <td class="adm-px-3 adm-py-2">
                          <input v-model="manualItemDraft.panFlag" class="tv-field adm-min-w-0" />
                        </td>
                        <td class="adm-px-3 adm-py-2">
                          <input v-model="manualItemDraft.seasonHint" class="tv-field adm-min-w-0" />
                        </td>
                        <td class="adm-px-3 adm-py-2">
                          <label class="enable-switch" title="自动失效">
                            <input v-model="manualItemDraft.autoDisable" type="checkbox" />
                            <span class="enable-slider"></span>
                          </label>
                        </td>
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500">待保存</td>
                        <td class="adm-px-3 adm-py-2" colspan="2">
                          <div class="adm-flex adm-items-center adm-gap-2">
                            <button
                              type="button"
                              class="action-btn green"
                              :disabled="manualItemSaving"
                              @click="saveManualItemDraft"
                            >保存</button>
                            <button
                              type="button"
                              class="action-btn gray"
                              :disabled="manualItemSaving"
                              @click="cancelManualItemEditor"
                            >取消</button>
                          </div>
                        </td>
                      </tr>
                      <tr v-if="manualTmdbSelectedReady && !manualItemRowsLoading && !manualItemRows.length && !manualItemEditorOpen">
                        <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="8">无数据</td>
                      </tr>
                      <tr v-for="row in manualItemRows" :key="`manual-item-${row.id}`">
                        <template v-if="manualItemEditingId === row.id">
                          <td class="adm-px-3 adm-py-2 adm-text-xs">
                            <input
                              v-if="isManualRowSourceMode(row)"
                              v-model="manualItemEditDraft.siteKey"
                              class="tv-field adm-min-w-0"
                            />
                            <span v-else>{{ row.siteKey || '-' }}</span>
                          </td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">
                            <input
                              v-if="isManualRowSourceMode(row)"
                              v-model="manualItemEditDraft.siteDetail"
                              class="tv-field adm-min-w-0"
                            />
                            <span v-else style="font-family:ui-monospace,SFMono-Regular,Menlo,monospace;">{{ row.siteDetail || '-' }}</span>
                          </td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">
                            <input
                              v-if="isManualRowPanMode(row)"
                              v-model="manualItemEditDraft.panFlag"
                              class="tv-field adm-min-w-0"
                            />
                            <span v-else>{{ row.panFlag || '-' }}</span>
                          </td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">
                            <input v-model="manualItemEditDraft.seasonHint" class="tv-field adm-min-w-0" />
                          </td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">
                            <label class="enable-switch" title="自动失效">
                              <input v-model="manualItemEditDraft.autoDisable" type="checkbox" />
                              <span class="enable-slider"></span>
                            </label>
                          </td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">待保存</td>
                          <td class="adm-px-3 adm-py-2" colspan="2">
                            <div class="adm-flex adm-items-center adm-gap-2">
                              <button
                                type="button"
                                class="action-btn green"
                                :disabled="manualItemUpdating"
                                @click="saveManualItemRowEditor(row)"
                              >
                                保存
                              </button>
                              <button
                                type="button"
                                class="action-btn gray"
                                :disabled="manualItemUpdating"
                                @click="cancelManualItemRowEditor"
                              >
                                取消
                              </button>
                            </div>
                          </td>
                        </template>
                        <template v-else>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">{{ row.siteKey || '-' }}</td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs" style="font-family:ui-monospace,SFMono-Regular,Menlo,monospace;">{{ row.siteDetail || '-' }}</td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">{{ row.panFlag || '-' }}</td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">{{ row.seasonHint || '-' }}</td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">{{ row.autoDisable ? '是' : '否' }}</td>
                          <td class="adm-px-3 adm-py-2 adm-text-xs">{{ row.enabled ? '启用' : '禁用' }}</td>
                          <td class="adm-px-3 adm-py-2" colspan="2">
                            <div class="adm-flex adm-items-center adm-gap-2">
                              <button
                                type="button"
                                class="action-btn blue"
                                :disabled="manualItemDeletingId === row.id"
                                @click="openManualItemRowEditor(row)"
                              >
                                修改
                              </button>
                              <button
                                type="button"
                                class="action-btn red"
                                :disabled="manualItemDeletingId === row.id"
                                @click="removeManualItemRow(row)"
                              >
                                删除
                              </button>
                            </div>
                          </td>
                        </template>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-flex adm-items-center adm-gap-3">
              <div class="adm-text-sm adm-font-semibold adm-text-gray-700">网盘映射设置</div>
            </div>
            <div class="adm-text-xs adm-text-gray-500">用于智能提取匹配别名</div>
            <div class="adm-space-y-2">
              <div class="smart-alias-add-row">
                <div class="smart-alias-add-pair">
                  <input
                    v-model="smartPanAliasMapPanInput"
                    class="tv-field adm-min-w-0"
                    :disabled="smartSaving"
                    placeholder="网盘（如：百度）"
                  />
                  <input
                    v-model="smartPanAliasMapAliasesInput"
                    class="tv-field adm-min-w-0"
                    :disabled="smartSaving"
                    placeholder="网盘名称（如：百度,baidu）"
                  />
                </div>
                <button type="button" class="btn-green" :disabled="smartSaving" @click="addSmartPanAliasMapping">添加</button>
              </div>
              <ul class="magic-rule-list">
                <li v-if="!smartPanAliasMappings.length" class="adm-text-gray-500 adm-text-sm">无数据</li>
                <li
                  v-for="(row, index) in smartPanAliasMappings"
                  :key="`smart-pan-alias-${index}`"
                  class="magic-rule-row"
                >
                  <div class="magic-rule-shell smart-alias-shell">
                    <div class="magic-rule-index">{{ index + 1 }}.</div>
                    <div class="magic-rule-pair">
                      <input v-model="smartPanAliasMappings[index].pan" class="tv-field adm-min-w-0" style="flex: 1 1 0;" />
                      <input v-model="smartPanAliasMappings[index].aliases" class="tv-field adm-min-w-0" style="flex: 2 1 0;" />
                    </div>
                    <div class="action-group">
                      <button type="button" class="action-btn green" :disabled="smartSaving" @click="saveSmartPanel('保存成功')">保存</button>
                      <button type="button" class="action-btn red" :disabled="smartSaving" @click="removeSmartPanAliasMapping(index)">删除</button>
                    </div>
                  </div>
                </li>
              </ul>
            </div>
          </div>

          <div class="adm-pt-1 adm-flex adm-justify-start">
            <button
              type="button"
              class="btn-green"
              :disabled="smartSaving || smartLoading"
              @click="saveSmartPanel('保存成功')"
            >
              {{ smartSaving ? '保存中' : '保存' }}
            </button>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'thirdparty'"
          id="dashboardThirdparty"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <PanelsIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            三方客户端设置
          </div>

          <div class="dashboard-card adm-space-y-4">
            <div class="adm-text-sm adm-text-gray-600">
              配置 Emby/Jellyfin 等三方客户端首页栏目（Emby 目前支持豆瓣模块/站点数据/历史记录；部分客户端会自动在栏目名前加“最新”，建议显示名称不要再以“最新”开头）。
            </div>

            <form class="adm-space-y-3" @submit.prevent="saveThirdpartyPanel">
              <div class="adm-flex adm-items-center adm-gap-2 adm-flex-wrap">
                <button type="button" class="btn-green" :disabled="thirdPartyBusy" @click="addThirdPartyHomeSection">添加栏目</button>
                <button type="button" class="btn-ghost-blue" :disabled="thirdPartyBusy" @click="restoreThirdPartyHomeSectionDefaults">恢复默认</button>
              </div>

              <div class="tv-panel tv-cpo-config-shell">
                <div class="tv-cpo-config-table thirdparty-config-table adm-overflow-x-auto">
                <table class="adm-table-auto adm-w-max adm-max-w-full adm-text-sm adm-text-left adm-text-gray-700 thirdparty-config-grid">
                  <thead class="table-head">
                    <tr>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">显示名称</th>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">主模块</th>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">栏目类型</th>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">站点选择</th>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">分类选择</th>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">卡片属性</th>
                      <th class="adm-px-3 adm-py-2 adm-whitespace-nowrap">操作</th>
                    </tr>
                  </thead>
                  <tbody class="adm-divide-y adm-divide-gray-100">
                    <tr v-if="thirdPartyLoading">
                      <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">加载中...</td>
                    </tr>
                    <tr v-else-if="!thirdPartyHomeSections.length">
                      <td class="adm-px-3 adm-py-2 adm-text-gray-500" colspan="7">无数据</td>
                    </tr>
                    <tr v-for="(row, index) in thirdPartyHomeSections" :key="row.id || `thirdparty-${index}`">
                      <td class="adm-px-3 adm-py-2">
                        <input
                          :value="row.name"
                          class="tv-field"
                          autocomplete="off"
                          :disabled="thirdPartyBusy"
                          @input="updateThirdPartyHomeSectionField(index, 'name', $event.target.value)"
                        />
                      </td>
                      <td class="adm-px-3 adm-py-2">
                        <div class="ui-selectbox ui-selectbox--compact thirdparty-selectbox">
                          <button
                            type="button"
                            class="ui-selectbox__trigger"
                            :disabled="thirdPartyBusy"
                            @click="toggleThirdPartyDropdown(index, 'module')"
                          >
                            {{ getThirdPartyOptionLabel(EMBY_MODULE_OPTIONS, row.module, '请选择') }}
                          </button>
                          <div v-if="thirdPartyDropdownOpenKey === `${index}:module`" class="ui-selectbox__menu">
                            <div
                              v-for="option in EMBY_MODULE_OPTIONS"
                              :key="`module-${option.value}`"
                              class="ui-selectbox__option"
                              :class="{ 'is-active': row.module === option.value }"
                              @click="selectThirdPartyModule(index, option.value)"
                            >
                              {{ option.label }}
                            </div>
                          </div>
                        </div>
                      </td>
                      <td class="adm-px-3 adm-py-2">
                        <div class="ui-selectbox ui-selectbox--compact thirdparty-selectbox">
                          <button
                            type="button"
                            class="ui-selectbox__trigger"
                            :disabled="thirdPartyBusy"
                            @click="toggleThirdPartyDropdown(index, 'mediaType')"
                          >
                            {{ getThirdPartyOptionLabel(EMBY_MEDIA_TYPE_OPTIONS, row.mediaType, '请选择') }}
                          </button>
                          <div v-if="thirdPartyDropdownOpenKey === `${index}:mediaType`" class="ui-selectbox__menu">
                            <div
                              v-for="option in EMBY_MEDIA_TYPE_OPTIONS"
                              :key="`media-${option.value}`"
                              class="ui-selectbox__option"
                              :class="{ 'is-active': row.mediaType === option.value }"
                              @click="updateThirdPartyHomeSectionField(index, 'mediaType', option.value, true)"
                            >
                              {{ option.label }}
                            </div>
                          </div>
                        </div>
                      </td>
                      <td class="adm-px-3 adm-py-2">
                        <div class="ui-selectbox ui-selectbox--compact thirdparty-selectbox">
                          <button
                            type="button"
                            class="ui-selectbox__trigger"
                            :disabled="thirdPartyBusy || !isThirdPartyModuleRequiringSite(row.module)"
                            @click="toggleThirdPartyDropdown(index, 'siteKey')"
                          >
                            {{ getThirdPartyOptionLabel(thirdPartyHomeSites, row.siteKey, '选择站点') }}
                          </button>
                          <div v-if="thirdPartyDropdownOpenKey === `${index}:siteKey`" class="ui-selectbox__menu">
                            <div class="ui-selectbox__option" :class="{ 'is-active': !row.siteKey }" @click="selectThirdPartySite(index, '')">选择站点</div>
                            <div
                              v-for="option in thirdPartyHomeSites"
                              :key="`site-${option.key}`"
                              class="ui-selectbox__option"
                              :class="{ 'is-active': row.siteKey === option.key }"
                              @click="selectThirdPartySite(index, option.key)"
                            >
                              {{ option.label }}
                            </div>
                          </div>
                        </div>
                      </td>
                      <td class="adm-px-3 adm-py-2">
                        <div class="ui-selectbox ui-selectbox--compact thirdparty-selectbox">
                          <button
                            type="button"
                            class="ui-selectbox__trigger"
                            :disabled="thirdPartyBusy || !isThirdPartyModuleRequiringSite(row.module) || !row.siteKey"
                            @click="toggleThirdPartyDropdown(index, 'categoryId')"
                          >
                            {{ getThirdPartyCategoryLabel(row.siteKey, row.categoryId) }}
                          </button>
                          <div v-if="thirdPartyDropdownOpenKey === `${index}:categoryId`" class="ui-selectbox__menu">
                            <div class="ui-selectbox__option" :class="{ 'is-active': !row.categoryId }" @click="selectThirdPartyCategory(index, '')">选择分类</div>
                            <div
                              v-for="option in getThirdPartyCategories(row.siteKey)"
                              :key="`cat-${row.siteKey}-${option.id}`"
                              class="ui-selectbox__option"
                              :class="{ 'is-active': row.categoryId === option.id }"
                              @click="selectThirdPartyCategory(index, option.id)"
                            >
                              {{ option.name }}
                            </div>
                          </div>
                        </div>
                      </td>
                      <td class="adm-px-3 adm-py-2">
                        <div class="ui-selectbox ui-selectbox--compact thirdparty-selectbox">
                          <button
                            type="button"
                            class="ui-selectbox__trigger"
                            :disabled="thirdPartyBusy || !isThirdPartyModuleRequiringSite(row.module)"
                            @click="toggleThirdPartyDropdown(index, 'cardStyle')"
                          >
                            {{ getThirdPartyOptionLabel(EMBY_CARD_STYLE_OPTIONS, row.cardStyle, '请选择') }}
                          </button>
                          <div v-if="thirdPartyDropdownOpenKey === `${index}:cardStyle`" class="ui-selectbox__menu">
                            <div
                              v-for="option in EMBY_CARD_STYLE_OPTIONS"
                              :key="`card-${option.value}`"
                              class="ui-selectbox__option"
                              :class="{ 'is-active': row.cardStyle === option.value }"
                              @click="updateThirdPartyHomeSectionField(index, 'cardStyle', option.value, true)"
                            >
                              {{ option.label }}
                            </div>
                          </div>
                        </div>
                      </td>
                      <td class="adm-px-3 adm-py-2 adm-whitespace-nowrap">
                        <div class="adm-flex adm-items-center adm-gap-2">
                          <button type="button" class="btn-ghost-blue" :disabled="thirdPartyBusy || index === 0" @click="moveThirdPartyHomeSection(index, -1)">上移</button>
                          <button type="button" class="btn-ghost-blue" :disabled="thirdPartyBusy || index === thirdPartyHomeSections.length - 1" @click="moveThirdPartyHomeSection(index, 1)">下移</button>
                          <button type="button" class="btn-ghost-red" :disabled="thirdPartyBusy" @click="removeThirdPartyHomeSection(index)">删除</button>
                        </div>
                      </td>
                    </tr>
                  </tbody>
                </table>
                </div>
              </div>

              <div class="adm-pt-1 adm-flex adm-justify-start">
                <button type="submit" class="btn-green" :disabled="thirdPartyBusy">
                  {{ thirdPartySaving ? '保存中' : '保存' }}
                </button>
              </div>
            </form>
          </div>
        </section>

        <section
          v-else-if="isAdmin && activeNavKey === 'metadata'"
          id="dashboardMetadata"
          class="dashboard-panel adm-space-y-6"
        >
          <div class="adm-flex adm-items-center adm-gap-2 adm-text-gray-800 adm-text-base adm-font-semibold">
            <DatabaseIcon class="lucide adm-h-5 adm-w-5 adm-text-gray-600" />
            媒体数据设置
          </div>

          <form @submit.prevent="saveMetadataPanel" class="adm-space-y-6">
            <div class="dashboard-card adm-space-y-4">
              <div class="adm-flex adm-items-center adm-gap-3">
                <div class="adm-text-sm adm-font-semibold adm-text-gray-700">豆瓣配置</div>
              </div>

              <div class="adm-space-y-4">
                <div class="adm-relative">
                  <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">豆瓣数据代理</label>
                  <div class="ui-selectbox ui-selectbox--compact" ref="doubanDataDropdownRef">
                    <button
                      type="button"
                      class="ui-selectbox__trigger"
                      :disabled="metadataLoading || metadataSaving"
                      @click="toggleDoubanDataDropdown"
                    >
                      {{ currentDoubanDataProxyLabel }}
                    </button>
                    <div v-if="doubanDataDropdownOpen" class="ui-selectbox__menu">
                      <div
                        v-for="option in doubanDataProxyOptions"
                        :key="option.value"
                        class="ui-selectbox__option"
                        :class="{ 'is-active': metadataForm.doubanDataProxy === option.value }"
                        @click="selectDoubanDataProxy(option.value)"
                      >
                        {{ option.label }}
                      </div>
                    </div>
                  </div>
                  <div v-if="metadataForm.doubanDataProxy === 'custom'" class="adm-mt-2">
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">豆瓣代理地址</label>
                    <input v-model="metadataForm.doubanDataCustom" class="tv-field" autocomplete="off" />
                    <p class="adm-text-xs adm-text-gray-500 adm-mt-1">自定义代理地址</p>
                  </div>
                </div>

                <div class="adm-relative">
                  <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">豆瓣图片代理</label>
                  <div class="ui-selectbox ui-selectbox--compact" ref="doubanImgDropdownRef">
                    <button
                      type="button"
                      class="ui-selectbox__trigger"
                      :disabled="metadataLoading || metadataSaving"
                      @click="toggleDoubanImgDropdown"
                    >
                      {{ currentDoubanImgProxyLabel }}
                    </button>
                    <div v-if="doubanImgDropdownOpen" class="ui-selectbox__menu">
                      <div
                        v-for="option in doubanImgProxyOptions"
                        :key="option.value"
                        class="ui-selectbox__option"
                        :class="{ 'is-active': metadataForm.doubanImgProxy === option.value }"
                        @click="selectDoubanImgProxy(option.value)"
                      >
                        {{ option.label }}
                      </div>
                    </div>
                  </div>
                  <div v-if="metadataForm.doubanImgProxy === 'custom'" class="adm-mt-2">
                    <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700 adm-mb-1">豆瓣图片代理地址</label>
                    <input v-model="metadataForm.doubanImgCustom" class="tv-field" autocomplete="off" />
                    <p class="adm-text-xs adm-text-gray-500 adm-mt-1">自定义图片代理服务器地址</p>
                  </div>
                </div>

                <div class="adm-space-y-1">
                  <label class="adm-block adm-text-sm adm-font-medium adm-text-gray-700">豆瓣搜索 Cookie</label>
                  <textarea
                    v-model="metadataForm.doubanSearchCookie"
                    class="tv-field tv-textarea"
                    rows="4"
                    autocomplete="off"
                    spellcheck="false"
                  />
                </div>
              </div>
            </div>

            <div class="dashboard-card adm-space-y-4">
              <div class="adm-flex adm-items-center adm-gap-3">
                <div class="adm-text-sm adm-font-semibold adm-text-gray-700">TMDB 设置</div>
              </div>

              <div class="adm-space-y-4">
                <div class="adm-space-y-1">
                  <div class="adm-text-sm adm-font-medium adm-text-gray-700">TMDB API TOKEN</div>
                  <input v-model="metadataForm.tmdbApiToken" class="tv-field" type="text" autocomplete="off" />
                  <div class="adm-text-xs adm-text-gray-500">填入 v3 或 v4 任意一种。</div>
                </div>

                <div class="adm-space-y-1">
                  <div class="adm-text-sm adm-font-medium adm-text-gray-700">TMDB 数据代理地址</div>
                  <input v-model="metadataForm.tmdbDataProxyBase" class="tv-field" type="text" autocomplete="off" />
                  <div class="adm-text-xs adm-text-gray-500">GoProxy:https://你的域名/tmdb/3</div>
                </div>

                <div class="adm-space-y-1">
                  <div class="adm-text-sm adm-font-medium adm-text-gray-700">TMDB 图片代理地址</div>
                  <input v-model="metadataForm.tmdbImageProxyBase" class="tv-field" type="text" autocomplete="off" />
                  <div class="adm-text-xs adm-text-gray-500">GoProxy:https://你的域名/tmdb-img</div>
                </div>

                <div class="adm-grid adm-gap-3 adm-grid-cols-sm-3">
                  <div class="adm-space-y-1">
                    <div class="adm-text-sm adm-font-medium adm-text-gray-700">语言</div>
                    <input v-model="metadataForm.language" class="tv-field" placeholder="zh-CN" autocomplete="off" />
                  </div>
                  <div class="adm-space-y-1">
                    <div class="adm-text-sm adm-font-medium adm-text-gray-700">地区</div>
                    <input v-model="metadataForm.region" class="tv-field" placeholder="CN" autocomplete="off" />
                  </div>
                  <div class="adm-space-y-1">
                    <div class="adm-text-sm adm-font-medium adm-text-gray-700">包含成人内容</div>
                    <div>
                      <label class="enable-switch" title="包含成人内容">
                        <input v-model="metadataForm.includeAdult" type="checkbox" />
                        <span class="enable-slider"></span>
                      </label>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div class="dashboard-card adm-space-y-4">
              <div class="adm-flex adm-items-center adm-gap-3">
                <div class="adm-text-sm adm-font-semibold adm-text-gray-700">缓存清理</div>
              </div>
              <div class="adm-flex adm-items-center adm-gap-3 adm-flex-wrap">
                <template v-if="metadataCacheConfirmingKey !== 'douban'">
                  <button
                    type="button"
                    class="btn-ghost-red"
                    :disabled="metadataPanelBusy || !!metadataCacheClearingKey"
                    @click="beginMetadataCacheClearConfirm('douban')"
                  >
                    清除豆瓣缓存
                  </button>
                </template>
                <template v-else>
                  <button
                    type="button"
                    class="btn-ghost-red"
                    :disabled="metadataPanelBusy || metadataCacheClearingKey === 'tmdb' || metadataCacheClearingKey === 'all'"
                    @click="confirmMetadataCacheClear('douban')"
                  >
                    {{ metadataCacheClearingKey === 'douban' ? '清理中' : '确定' }}
                  </button>
                  <button
                    type="button"
                    class="btn-ghost-blue"
                    :disabled="!!metadataCacheClearingKey"
                    @click="cancelMetadataCacheClearConfirm"
                  >
                    取消
                  </button>
                </template>

                <template v-if="metadataCacheConfirmingKey !== 'tmdb'">
                  <button
                    type="button"
                    class="btn-ghost-red"
                    :disabled="metadataPanelBusy || !!metadataCacheClearingKey"
                    @click="beginMetadataCacheClearConfirm('tmdb')"
                  >
                    清除TMDB缓存
                  </button>
                </template>
                <template v-else>
                  <button
                    type="button"
                    class="btn-ghost-red"
                    :disabled="metadataPanelBusy || metadataCacheClearingKey === 'douban' || metadataCacheClearingKey === 'all'"
                    @click="confirmMetadataCacheClear('tmdb')"
                  >
                    {{ metadataCacheClearingKey === 'tmdb' ? '清理中' : '确定' }}
                  </button>
                  <button
                    type="button"
                    class="btn-ghost-blue"
                    :disabled="!!metadataCacheClearingKey"
                    @click="cancelMetadataCacheClearConfirm"
                  >
                    取消
                  </button>
                </template>

                <template v-if="metadataCacheConfirmingKey !== 'all'">
                  <button
                    type="button"
                    class="btn-ghost-red"
                    :disabled="metadataPanelBusy || !!metadataCacheClearingKey"
                    @click="beginMetadataCacheClearConfirm('all')"
                  >
                    清除所有缓存
                  </button>
                </template>
                <template v-else>
                  <button
                    type="button"
                    class="btn-ghost-red"
                    :disabled="metadataPanelBusy || metadataCacheClearingKey === 'douban' || metadataCacheClearingKey === 'tmdb'"
                    @click="confirmMetadataCacheClear('all')"
                  >
                    {{ metadataCacheClearingKey === 'all' ? '清理中' : '确定' }}
                  </button>
                  <button
                    type="button"
                    class="btn-ghost-blue"
                    :disabled="!!metadataCacheClearingKey"
                    @click="cancelMetadataCacheClearConfirm"
                  >
                    取消
                  </button>
                </template>
              </div>
            </div>

            <div class="adm-pt-1 adm-flex adm-justify-start">
              <button type="submit" class="btn-green" :disabled="metadataLoading || metadataSaving">
                {{ metadataSaving ? '保存中' : '保存' }}
              </button>
            </div>
          </form>
        </section>

        <section
          v-else-if="isAdmin"
          class="dashboard-panel"
        ></section>

        <section
          v-else
          class="dashboard-panel"
        ></section>
      </div>
    </div>
    <TMDBRecognizeDialog
      v-model="manualTmdbDialogOpen"
      :keyword="manualTmdbDialogKeyword"
      :bootstrap="props.bootstrap"
      :confirm-busy="manualTmdbAdding"
      confirm-busy-text="添加中"
      :auto-close-on-select="false"
      @select="confirmManualTmdbSelectionByDialog"
    />
    <div v-if="toasts.length" class="mf-toast-container">
      <div
        v-for="toast in toasts"
        :key="toast.id"
        class="mf-toast"
        :class="`mf-toast--${toast.type}`"
      >
        <div class="mf-toast-bar"></div>
        <div class="mf-toast-icon" v-html="toastIconSvg(toast.type)"></div>
        <div class="mf-toast-body">
          <div class="mf-toast-text">{{ toast.message }}</div>
        </div>
        <button type="button" class="mf-toast-close" aria-label="关闭" @click="removeToast(toast.id)">
          <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M18 6 6 18"></path>
            <path d="M6 6l12 12"></path>
          </svg>
        </button>
      </div>
    </div>
  </div>
</template>

<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import {
  addDashboardUser,
  applyDashboardVideoSourceAutoResults,
  buildBackupFilename,
  checkDashboardVideoSourceSites,
  deleteSmartMatchBlockItem,
  deleteSmartMatchBlockKeyword,
  deleteDashboardCatpawrunnerServer,
  fetchPanSettings,
  fetchCatpawrunnerAdminSettings,
  fetchCatpawrunnerFullConfig,
  fetchCatpawrunnerWebsitePans,
  fetchSmartMatchBlockItems,
  fetchSmartMatchBlockKeywords,
  fetchDashboardSmartManualTMDBItems,
  fetchDashboardSmartManualItems,
  fetchSmartSettings,
  fetchThirdpartySettings,
  fetchThirdpartySiteCategories,
  fetchDashboardVideoPans,
  fetchDashboardVideoSourceSites,
  fetchMagicSettings,
  fetchMetadataSettings,
  deleteDashboardUser,
  downloadJsonFile,
  exportDashboardBackup,
  importDashboardVideoSourceSites,
  pollPanQrLogin,
  fetchSiteSettings,
  fetchUsers,
  normalizeHttpBase,
  probeDashboardVideoSourceSites,
  probeRelayVersion,
  probeGoProxyVersion,
  resolveDashboardCatpawrunnerApiBase,
  restoreDashboardBackup,
  saveCatpawrunnerAdminSettings,
  updateCatpawrunnerOnlineConfig,
  saveCatpawrunnerWebsitePans,
  saveDashboardCatpawrunnerServer,
  saveDashboardRelaySettings,
  saveDashboardGoProxySettings,
  saveDashboardVideoPans,
  saveDashboardVideoSourceOrder,
  saveGlobalSiteSettings,
  saveMagicSettings,
  saveSmartSettings,
  saveThirdpartySettings,
  addDashboardSmartManualTMDBItem,
  deleteDashboardSmartManualTMDBItem,
  addDashboardSmartManualItem,
  updateDashboardSmartManualItem,
  deleteDashboardSmartManualItem,
  clearDoubanMetadataCache,
  clearTMDBMetadataCache,
  clearAllMetadataCache,
  savePanLoginSettings,
  startPanQrLogin,
  syncPanLoginSettingsToCatpawrunner,
  toggleDashboardUserStatus,
  unwrapCatpawrunnerWebsiteData,
  updateDashboardVideoSourceCover,
  updateDashboardVideoSourceHome,
  updateDashboardVideoSourceSearch,
  updateDashboardVideoSourceStatus,
  updateDashboardUser,
  saveMetadataSettings,
  validateSearchDisplayMode
} from './dashboardLogic';
import {
  CloudIcon,
  DatabaseIcon,
  FilmIcon,
  LinkIcon,
  PanelsIcon,
  SettingsIcon,
  SparklesIcon,
  UserIcon,
  WandIcon,
} from './dashboardIcons';
import { normalizeSeasonEpisodeMarkers } from '../../shared/episodeMarkerNormalize';
import MediaCard from '../../shared/MediaCard.vue';
import TMDBRecognizeDialog from '../../shared/TMDBRecognizeDialog.vue';

const props = defineProps({
  bootstrap: {
    type: Object,
    required: true
  }
});

const adminNavItems = [
  { key: 'site', label: '全局设置', icon: SettingsIcon },
  { key: 'user', label: '用户管理', icon: UserIcon },
  { key: 'pan', label: '网盘设置', icon: CloudIcon },
  { key: 'interface', label: '接口设置', icon: LinkIcon },
  { key: 'video', label: '视频源管理', icon: FilmIcon },
  { key: 'magic', label: '魔法匹配设置', icon: WandIcon },
  { key: 'smart', label: '智能播放设置', icon: SparklesIcon },
  { key: 'metadata', label: '媒体数据设置', icon: DatabaseIcon },
  { key: 'thirdparty', label: '三方客户端设置', icon: PanelsIcon }
];

const searchDisplayOptions = [
  { value: 'tmdb', label: 'TMDB' },
  { value: 'sites', label: '视频源' },
  { value: 'both', label: '展示两者' }
];

const doubanDataProxyOptions = [
  { value: 'server-proxy', label: '服务器代理' },
  { value: 'cors', label: 'Cors Proxy By Zwei' },
  { value: 'cdn-tx', label: '豆瓣 CDN By CMLiussss（腾讯云）' },
  { value: 'cdn-ali', label: '豆瓣 CDN By CMLiussss（阿里云）' },
  { value: 'custom', label: '自定义代理' }
];

const doubanImgProxyOptions = [
  { value: 'server-proxy', label: '服务器代理' },
  { value: 'douban-cdn-ali', label: '豆瓣官方精品 CDN（阿里云）' },
  { value: 'cdn-tx', label: '豆瓣 CDN By CMLiussss（腾讯云）' },
  { value: 'cdn-ali', label: '豆瓣 CDN By CMLiussss（阿里云）' },
  { value: 'custom', label: '自定义代理' }
];

const DEFAULT_EPISODE_RULES = [
  {
    pattern: '.*?(?:(?:[Ss](?:eason)?\\s*0*(\\d{1,2}))|(?:第\\s*0*(\\d{1,2})\\s*(?:季|部|篇))).*?(?:第\\s*0*(\\d{1,4})\\s*(?:集|话)|[Ee][Pp]?\\s*0*(\\d{1,4})(?:$|\\D)).*?.*',
    replace: 'S$1$2E$3$4',
    flags: 'i'
  },
  {
    pattern: '.*?([Ss]\\d{1,2})?(?:第\\s*(\\d{1,4})\\s*(?:集|话)|[Ee][Pp]?\\s*(\\d{1,4})(?:$|\\D)).*?.*',
    replace: '$1E$2$3',
    flags: 'i'
  },
  {
    pattern: '^[\\s\\[\\]\\(\\){}【】._=-]*0*(\\d{1,4})[\\s\\[\\]\\(\\){}【】._=-]*(?:\\.[A-Za-z0-9]{1,6})?\\s*$',
    replace: 'E$1',
    flags: 'i'
  }
];

const DEFAULT_MOVIE_RULES = [
  {
    pattern: '^\\s*(?!.*(?:S\\d{1,2}\\s*E\\d{1,3}|第\\s*\\d+\\s*[集话期]|(?:^|[\\s._-])(?:EP?|E)\\s*\\d+(?:$|[\\s._-])))(?=.*\\b(?:19\\d{2}|20\\d{2})\\b).*\\.(?:mkv|mp4)\\s*$',
    replace: '',
    flags: 'i'
  }
];

const DEFAULT_EPISODE_CLEAN_RULES = [
  String.raw`\[(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\])[^\]]*\]`,
  String.raw`【(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*】)[^】]*】`,
  String.raw`\((?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*\))[^)]*\)`,
  String.raw`（(?!\s*[Ss]\d{1,2}(?:\s*[Ee]\d{1,5})?\s*）)[^）]*）`,
  String.raw`(?:^|[\s\[\]\(\){}【】._-])(?:4k|8k|2160p|1080p|720p)(?=$|[\s\[\]\(\){}【】._-])`,
  String.raw`高\s*码\s*(?:率|资源|直链)?|码\s*率`
];

const DEFAULT_AGGREGATE_REGEX_RULES = [
  String.raw`\([^)]*\)|（[^）]*）|\[[^\]]*\]|\{[^}]*\}|【[^】]*】`,
  String.raw`(?<!新)年\s*番\s*\d+|(?<!新)年\s*番`,
  String.raw`更新\s*中|(?:更新(?:至|到)?|更(?:至|到)?|更|首\s*更)\s*(?:第\s*)?(?:EP?|E)?\s*(?:\d{1,4}|[一二三四五六七八九十百千万两零〇]{1,20})(?:\s*(?:\+|＋|多|余))?\s*(?:集|话|期)?|首\s*更`,
  String.raw`(?:HD\s*)?(?:4[kK]|8[kK])|(?:2160|1080|720)[pP]|国\s*漫|臻\s*彩|杜\s*比\s*音\s*效|已\s*刮\s*削|连\s*载\s*中|10\s*[- ]?bit`,
  String.raw`(?:19\d{2}|20\d{2})(?=\s*(?:(?:HD\s*)?(?:4[kK]|8[kK])|(?:更新|更)))`,
  String.raw`最\s*新\s*(?:一\s*集|更\s*新)`,
  String.raw`(?<=\D)\d{1,4}$`
];

const isAdmin = computed(() => props.bootstrap && props.bootstrap.user && props.bootstrap.user.role === 'admin');
const activeNavKey = ref('site');
const DASHBOARD_ACTIVE_NAV_STORAGE_KEY = 'meowfilm_dashboard_active_nav';
const siteLoading = ref(false);
const userLoading = ref(false);
const globalSaving = ref(false);
const userAddSubmitting = ref(false);
const userEditSubmitting = ref(false);
const interfaceLoading = ref(false);
const backupExporting = ref(false);
const backupImporting = ref(false);
const metadataLoading = ref(false);
const metadataSaving = ref(false);
const metadataCacheConfirmingKey = ref('');
const metadataCacheClearingKey = ref('');
const thirdPartyLoading = ref(false);
const thirdPartySaving = ref(false);
const searchDisplayDropdownOpen = ref(false);
const searchDisplayDropdownRef = ref(null);
const doubanDataDropdownOpen = ref(false);
const doubanDataDropdownRef = ref(null);
const doubanImgDropdownOpen = ref(false);
const doubanImgDropdownRef = ref(null);
const thirdPartyDropdownOpenKey = ref('');
const backupImportFileRef = ref(null);
const panTabsRef = ref(null);
const catServerDropdownRef = ref(null);
const catSyncFromServerDropdownRef = ref(null);
const toasts = ref([]);
const users = ref(Array.isArray(props.bootstrap.users) ? props.bootstrap.users.map(normalizeUserRow) : []);
const userRowBusy = ref('');
const addUserFormOpen = ref(false);
const editingUsername = ref('');
const panLoading = ref(false);
const panSaveBusy = ref(false);
const activePanSettingKey = ref('baidu');
const panSettingsStore = ref({});
const loadedPanSettingKeys = new Set();
const catServers = ref([]);
const catSelectedServerKey = ref('');
const catServerPrevSelectedKey = ref('');
const catServerDropdownOpen = ref(false);
const catServerAddMode = ref(false);
const catSyncFromServerKey = ref('');
const catSyncFromServerDropdownOpen = ref(false);
const catDeleting = ref(false);
const catSaving = ref(false);
const catSyncPanBusy = ref(false);
const catRemoteLoading = ref(false);
const catConfigEditorOpen = ref(false);
const catConfigEditorMode = ref('create');
const catConfigEditorIndex = ref(-1);
const catConfigUpdateBusyKey = ref('');
const catConfigEditorForm = ref({
  name: '',
  url: ''
});
const catOnlineConfigs = ref([]);
const catPansExpanded = ref(false);
const catPans = ref([]);
const catPansSaving = ref(false);
const catPanNameWidthPx = ref(80);
const catForm = ref({
  name: '',
  apiBase: '',
  proxy: '',
  panBuiltinResolverEnabled: true,
  panMockEnabled: false,
  disableProxy: false,
  goProxyApi: ''
});
const goProxySaving = ref(false);
const goProxyForm = ref({
  enabled: false,
  autoSelect: false
});
const goProxyServers = ref([]);
const goProxyProbes = ref({});
const goProxyEditorOpen = ref(false);
const goProxyEditorMode = ref('create');
const goProxyEditorOriginalBase = ref('');
const goProxyEditorForm = ref({
  name: '',
  displayName: '',
  base: '',
  pans: { baidu: true, quark: true }
});
const relaySaving = ref(false);
const relayForm = ref({
  enabled: false,
  auth: ''
});
const relayServers = ref([]);
const relayProbes = ref({});
const relayEditorOpen = ref(false);
const relayEditorMode = ref('create');
const relayEditorOriginalBase = ref('');
const relayEditorForm = ref({
  name: '',
  displayName: '',
  base: '',
  secret: ''
});
const videoLoading = ref(false);
const videoImporting = ref(false);
const videoSitesExpanded = ref(false);
const videoSourceImportFileRef = ref(null);
const videoSourceSites = ref([]);
const videoSourceCoverSite = ref('');
const selectedVideoSourceKeys = ref([]);
const videoSourceNameWidthPx = ref(80);
const videoSourceApiWidthPx = ref(90);
const magicLoading = ref(false);
const magicSaving = ref(false);
const magicEpisodeDefaultsConfirming = ref(false);
const magicMovieDefaultsConfirming = ref(false);
const magicAggregateDefaultsConfirming = ref(false);
const magicEpisodeCleanRegexRules = ref([]);
const magicEpisodeRules = ref([]);
const magicMovieRules = ref([]);
const magicAggregateRegexRules = ref([]);
const magicEpisodeCleanRuleInput = ref('');
const magicEpisodeRulePatternInput = ref('');
const magicEpisodeRuleReplaceInput = ref('');
const magicMovieRulePatternInput = ref('');
const magicMovieRuleReplaceInput = ref('');
const magicAggregateRuleInput = ref('');
const magicEpisodeRuleTestInput = ref('');
const magicMovieRuleTestInput = ref('');
const magicAggregateRuleTestInput = ref('');
const magicAggregateRuleTestQueryInput = ref('');
const magicEpisodeRuleTestOutput = ref({ type: '', text: '' });
const magicMovieRuleTestOutput = ref({ type: '', text: '' });
const magicAggregateRuleTestOutput = ref({ type: '', text: '' });
const smartLoading = ref(false);
const smartSaving = ref(false);
const smartPanDefaultsConfirming = ref(false);
const smartSiteCleanDefaultsConfirming = ref(false);
const smartSourceRuleRows = ref(buildDefaultSmartSourceRuleRows());
const smartSourcePriorityTokensInput = ref('');
const smartPanMatchTokensInput = ref('');
const smartSiteCleanKeywordsInput = ref('');
const smartPanAliasMapPanInput = ref('');
const smartPanAliasMapAliasesInput = ref('');
const smartPanAliasMappings = ref([]);
const smartMatchBlockKeywords = ref([]);
const smartMatchBlockSelectedKeyword = ref('');
const smartMatchBlockItems = ref([]);
const smartMatchBlockKeywordsLoading = ref(false);
const smartMatchBlockItemsLoading = ref(false);
const manualTmdbDialogOpen = ref(false);
const manualTmdbDialogKeyword = ref('');
const manualTmdbItemsLoading = ref(false);
const manualTmdbAdding = ref(false);
const manualTmdbItems = ref([]);
const manualTmdbSelectedTmdbType = ref('');
const manualTmdbSelectedTmdbId = ref(0);
const manualTmdbDeletingKey = ref('');
const manualTmdbSelectedKey = computed(() => {
  const type = String(manualTmdbSelectedTmdbType.value || '').trim().toLowerCase();
  const id = Number.isFinite(Number(manualTmdbSelectedTmdbId.value))
    ? Math.trunc(Number(manualTmdbSelectedTmdbId.value))
    : 0;
  if ((type !== 'tv' && type !== 'movie') || id <= 0) return '';
  return `${type}:${id}`;
});
const manualTmdbSelectedReady = computed(() => !!manualTmdbSelectedKey.value);
const manualItemRowsLoading = ref(false);
const manualItemRows = ref([]);
const manualItemEditorOpen = ref(false);
const manualItemSaving = ref(false);
const manualItemUpdating = ref(false);
const manualItemDeletingId = ref(0);
const manualItemEditingId = ref(0);
const manualItemDraft = ref({
  siteKey: '',
  siteDetail: '',
  panFlag: '',
  seasonHint: '',
  autoDisable: true
});
const manualItemEditDraft = ref({
  siteKey: '',
  spiderApi: '',
  siteDetail: '',
  panFlag: '',
  seasonHint: '',
  autoDisable: true
});
const manualDetailColWidth = computed(() => {
  const rows = Array.isArray(manualItemRows.value) ? manualItemRows.value : [];
  let longest = 0;
  rows.forEach((row) => {
    const value = typeof row?.siteDetail === 'string' ? row.siteDetail.trim() : '';
    if (!value) return;
    if (value.length > longest) longest = value.length;
  });
  const width = Math.min(34, Math.max(8, longest + 2));
  return `${width}ch`;
});
const manualPanFlagColWidth = computed(() => {
  const rows = Array.isArray(manualItemRows.value) ? manualItemRows.value : [];
  let longest = 0;
  rows.forEach((row) => {
    const value = typeof row?.panFlag === 'string' ? row.panFlag.trim() : '';
    if (!value) return;
    if (value.length > longest) longest = value.length;
  });
  const width = Math.min(22, Math.max(8, longest + 2));
  return `${width}ch`;
});
const manualColumnLayoutStyle = computed(() => ({
  '--manual-col-detail': manualDetailColWidth.value,
  '--manual-col-panflag': manualPanFlagColWidth.value,
}));
const smartLoaded = ref(false);
const thirdPartyLoaded = ref(false);
const thirdPartyHomeSites = ref([]);
const thirdPartyHomeSections = ref([]);
const thirdPartySiteCategories = ref({});

function sanitizeMagicDisplayTitle(value) {
  return String(value || '')
    .replace(/[\u200b\u200c\u200d\ufeff]+/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function stripMagicEmojiSymbols(value) {
  return String(value || '').replace(
    /[\p{Extended_Pictographic}\p{Regional_Indicator}\u2600-\u27BF\uFE0F]/gu,
    ''
  );
}

function normalizeMagicAggregateDisplay(value) {
  return stripMagicEmojiSymbols(sanitizeMagicDisplayTitle(value)).replace(/\s+/g, ' ').trim();
}

function normalizeMagicAggregateKey(value) {
  return String(value || '')
    .toLowerCase()
    .replace(/[\u200b\u200c\u200d\ufeff]+/g, '')
    .replace(/[\s.\-_,，:：;；!！?？·•/\\|]+/g, '')
    .trim();
}

function runMagicAggregateNormalization({ query = '', title = '', rules = [] } = {}) {
  const trailingMatch = String(query || '').trim().match(/(\d+)\s*$/);
  const trailingDigits = trailingMatch ? String(trailingMatch[1] || '') : '';
  let output = stripMagicEmojiSymbols(sanitizeMagicDisplayTitle(title));
  const failures = [];
  const previews = [];
  (Array.isArray(rules) ? rules : []).forEach((rule, index) => {
    const source = String(rule || '').trim();
    if (!source) {
      previews[index] = normalizeMagicAggregateDisplay(output);
      return;
    }
    try {
      const regex = buildRegexFromInput(source, { defaultFlags: 'ig', forceGlobal: true });
      if (!regex) throw new Error('invalid');
      if (trailingDigits) {
        try {
          const probe1 = `x${trailingDigits}`;
          const probe2 = `x${trailingDigits}y`;
          const replaced1 = probe1.replace(regex, '');
          const replaced2 = probe2.replace(regex, '');
          const isTrailingDigitsRule = replaced1 === 'x' && replaced2 === probe2;
          if (isTrailingDigitsRule) {
            previews[index] = `${normalizeMagicAggregateDisplay(output)}（跳过尾号规则）`;
            return;
          }
        } catch (_error) {}
      }
      output = output.replace(regex, '');
      previews[index] = normalizeMagicAggregateDisplay(output);
    } catch (_error) {
      failures.push(`#${index + 1} 正则无效`);
      previews[index] = '正则无效';
    }
  });
  return {
    cleanedTitle: normalizeMagicAggregateDisplay(output),
    groupKey: normalizeMagicAggregateKey(output),
    trailingDigits,
    failures,
    previews,
  };
}

const magicEpisodeCleanRulePreviews = computed(() => {
  let output = normalizeSeasonEpisodeMarkers(String(magicEpisodeRuleTestInput.value || '').trim());
  return magicEpisodeCleanRegexRules.value.map((rule) => {
    const regex = buildRegexFromInput(rule, { defaultFlags: 'ig', forceGlobal: true });
    if (!regex) return '正则无效';
    try {
      output = output.replace(regex, '');
    } catch (_error) {}
    return String(output || '').replace(/\s+/g, ' ').trim();
  });
});
const magicAggregateRulePreviews = computed(() => {
  return runMagicAggregateNormalization({
    query: magicAggregateRuleTestQueryInput.value,
    title: magicAggregateRuleTestInput.value,
    rules: magicAggregateRegexRules.value,
  }).previews;
});
const magicShadowSmartSettings = ref({
  smartSourcePriorityTokens: [],
  smartPanMatchTokens: [],
  smartPanAliasMappings: [],
  smartSourceRuleRows: []
});

const DEFAULT_SMART_SOURCE_PRIORITY_TOKENS = [];
const DEFAULT_SMART_PAN_MATCH_TOKENS = ['移动', '天翼', '夸克', 'uc', '百度', '115'];
const DEFAULT_SMART_PAN_ALIAS_MAPPINGS = [
  { pan: '百度', aliases: '百度,baidu' },
  { pan: '夸克', aliases: '夸克,quark,夸父' },
  { pan: 'uc', aliases: 'uc,优夕' },
  { pan: '天翼', aliases: '天翼,天意,189' },
  { pan: '移动', aliases: '移动,139,逸动' },
  { pan: '115', aliases: '115,Pan115' }
];
const DEFAULT_SMART_SITE_CLEAN_KEYWORDS = ['直播', '体育', '短剧', '听书', '舞曲', '哔哩'];
const DEFAULT_EMBY_HOME_SECTIONS = [
  { id: 'view_history', name: '历史', module: 'history', mediaType: 'tv', siteKey: '', categoryId: '', cardStyle: '' },
  { id: 'view_tmdb_tv', name: '剧集', module: 'douban_tv', mediaType: 'tv', siteKey: '', categoryId: '', cardStyle: '' },
  { id: 'view_tmdb_movies', name: '电影', module: 'douban_movie', mediaType: 'movie', siteKey: '', categoryId: '', cardStyle: '' },
  { id: 'view_tmdb_anime', name: '动漫', module: 'bangumi_anime', mediaType: 'tv', siteKey: '', categoryId: '', cardStyle: '' },
  { id: 'view_tmdb_show', name: '综艺', module: 'douban_variety', mediaType: 'tv', siteKey: '', categoryId: '', cardStyle: '' }
];

const EMBY_MODULE_OPTIONS = [
  { value: 'douban_tv', label: '豆瓣剧集' },
  { value: 'douban_movie', label: '豆瓣电影' },
  { value: 'bangumi_anime', label: 'Bangumi动漫' },
  { value: 'douban_variety', label: '豆瓣综艺' },
  { value: 'history', label: '历史记录' },
  { value: 'site_data', label: '站点数据' }
];

const EMBY_MEDIA_TYPE_OPTIONS = [
  { value: 'tv', label: '电视剧' },
  { value: 'movie', label: '电影' }
];

const EMBY_CARD_STYLE_OPTIONS = [
  { value: 'tmdb', label: 'TMDB' },
  { value: 'site', label: '站点数据' }
];

const panQrStates = ref({
  baidu: { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' },
  quark: { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' },
  uc: { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' },
  '115': { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' },
  bili: { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' }
});

const siteForm = ref({
  siteName: props.bootstrap.siteName || '',
  searchDisplayMode: 'sites',
  netdiskProxyEnabled: false,
  netdiskProxyUrl: ''
});

const metadataForm = ref({
  doubanDataProxy: 'server-proxy',
  doubanDataCustom: '',
  doubanImgProxy: 'server-proxy',
  doubanImgCustom: '',
  doubanSearchCookie: '',
  tmdbApiToken: '',
  tmdbDataProxyBase: '',
  tmdbImageProxyBase: '',
  language: 'zh-CN',
  region: 'CN',
  includeAdult: false
});

const searchDisplayModeError = ref('');
const addUserForm = ref({
  username: '',
  password: ''
});
const editUserForm = ref({
  newUsername: '',
  newPassword: ''
});

const panSettingDefs = [
  { key: 'baidu', name: '百度', type: 'cookie' },
  { key: 'quark', name: '夸克', type: 'cookie' },
  { key: 'quark_tv', name: '夸克TV', type: 'quark_tv' },
  { key: '189', name: '天翼', type: 'account' },
  { key: '139', name: '移动', type: 'authorization' },
  { key: 'uc', name: 'UC', type: 'cookie' },
  { key: 'uc_tv', name: 'UC_TV', type: 'uc_tv' },
  { key: 'pan123', name: '123', type: 'account' },
  { key: '115', name: '115', type: 'cookie' },
  { key: 'bili', name: 'Bilibili', type: 'cookie' },
  { key: 'wuming', name: '观影', type: 'cookie' },
  { key: 'yunchao', name: '云巢', type: 'account' },
  { key: 'pan123ziyuan', name: '123资源网', type: 'cookie' }
];

const userCount = computed(() => users.value.length);
const canSubmitAddUser = computed(() => {
  return addUserForm.value.username.trim().length > 0 && addUserForm.value.password.trim().length > 0;
});
const canSubmitUserEdit = computed(() => {
  return editUserForm.value.newUsername.trim().length > 0 || editUserForm.value.newPassword.trim().length > 0;
});

const currentSearchDisplayLabel = computed(() => {
  const matched = searchDisplayOptions.find((option) => option.value === siteForm.value.searchDisplayMode);
  return matched ? matched.label : '请选择';
});

const currentDoubanDataProxyLabel = computed(() => {
  const matched = doubanDataProxyOptions.find((option) => option.value === metadataForm.value.doubanDataProxy);
  return matched ? matched.label : '请选择';
});

const currentDoubanImgProxyLabel = computed(() => {
  const matched = doubanImgProxyOptions.find((option) => option.value === metadataForm.value.doubanImgProxy);
  return matched ? matched.label : '请选择';
});

const metadataPanelBusy = computed(() => metadataLoading.value || metadataSaving.value);
const thirdPartyBusy = computed(() => thirdPartyLoading.value || thirdPartySaving.value);

const metadataCacheActionLabels = {
  douban: '豆瓣缓存',
  tmdb: 'TMDB缓存',
  all: '所有缓存'
};

const smartSiteCleanKeywordTokens = computed(() => normalizeCommaTokenLine(smartSiteCleanKeywordsInput.value));

const activePanSettingDef = computed(() => {
  return panSettingDefs.find((item) => item.key === activePanSettingKey.value) || panSettingDefs[0] || null;
});

const activePanSettingValue = computed(() => {
  const key = activePanSettingKey.value;
  const store = panSettingsStore.value && typeof panSettingsStore.value === 'object' ? panSettingsStore.value : {};
  const value = store[key];
  return value && typeof value === 'object' ? value : {};
});

const activePanTextValue = computed({
  get() {
    if (!activePanSettingDef.value) return '';
    if (activePanSettingDef.value.type === 'authorization') {
      return typeof activePanSettingValue.value.authorization === 'string' ? activePanSettingValue.value.authorization : '';
    }
    return typeof activePanSettingValue.value.cookie === 'string' ? activePanSettingValue.value.cookie : '';
  },
  set(value) {
    patchActivePanSetting(
      activePanSettingDef.value && activePanSettingDef.value.type === 'authorization'
        ? { authorization: String(value || '') }
        : { cookie: String(value || '') }
    );
  }
});

const activePanAccount = computed({
  get() {
    return {
      username: typeof activePanSettingValue.value.username === 'string' ? activePanSettingValue.value.username : '',
      password: typeof activePanSettingValue.value.password === 'string' ? activePanSettingValue.value.password : '',
      cookie: typeof activePanSettingValue.value.cookie === 'string' ? activePanSettingValue.value.cookie : ''
    };
  },
  set(value) {
    patchActivePanSetting({
      username: value && typeof value.username === 'string' ? value.username : '',
      password: value && typeof value.password === 'string' ? value.password : '',
      cookie: value && typeof value.cookie === 'string' ? value.cookie : ''
    });
  }
});

const activePanTv = computed({
  get() {
    return {
      refresh_token: typeof activePanSettingValue.value.refresh_token === 'string' ? activePanSettingValue.value.refresh_token : '',
      device_id: typeof activePanSettingValue.value.device_id === 'string' ? activePanSettingValue.value.device_id : '',
      access_token: typeof activePanSettingValue.value.access_token === 'string' ? activePanSettingValue.value.access_token : '',
      access_token_exp_at: typeof activePanSettingValue.value.access_token_exp_at === 'string' ? activePanSettingValue.value.access_token_exp_at : ''
    };
  },
  set(value) {
    patchActivePanSetting({
      refresh_token: value && typeof value.refresh_token === 'string' ? value.refresh_token : '',
      device_id: value && typeof value.device_id === 'string' ? value.device_id : '',
      access_token: value && typeof value.access_token === 'string' ? value.access_token : '',
      access_token_exp_at: value && typeof value.access_token_exp_at === 'string' ? value.access_token_exp_at : ''
    });
  }
});

const activeQrState = computed(() => {
  const key = activePanSettingKey.value;
  return panQrStates.value[key] || { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' };
});

const activeQrButtonLabel = computed(() => {
  const phase = activeQrState.value && typeof activeQrState.value.phase === 'string'
    ? activeQrState.value.phase
    : 'idle';
  if (phase === 'starting') return '生成二维码中...';
  if (phase === 'pending') return '二维码已生成，等待扫码确认...';
  if (phase === 'scanned') return '已扫码：请在手机端确认登录...';
  return '二维码登录';
});

const activeQrButtonBusy = computed(() => {
  const phase = activeQrState.value && typeof activeQrState.value.phase === 'string'
    ? activeQrState.value.phase
    : 'idle';
  return phase === 'starting' || phase === 'pending' || phase === 'scanned';
});

const catServerOptions = computed(() => {
  const options = catServers.value.map((server) => ({
    key: server.name || '',
    label: server.name || '未命名服务器'
  }));
  if (catServerAddMode.value) {
    return [{ key: '__new__', label: '新建服务器' }, ...options];
  }
  return options;
});

const selectedCatServer = computed(() => {
  return catServers.value.find((server) => server.name === catSelectedServerKey.value) || null;
});

const selectedCatServerLabel = computed(() => {
  if (catServerAddMode.value) return '新建服务器';
  if (!catServerOptions.value.length) return '暂无数据';
  const matched = catServerOptions.value.find((item) => item.key === catSelectedServerKey.value);
  return matched ? matched.label : catServerOptions.value[0].label;
});

const savedSelectedCatApiBase = computed(() => {
  const current = selectedCatServer.value;
  return normalizeHttpBase(current && current.apiBase ? current.apiBase : '');
});

const normalizedCatApiBase = computed(() => normalizeHttpBase(catForm.value.apiBase));

const catApiBaseChanged = computed(() => {
  if (catServerAddMode.value) return false;
  if (!selectedCatServer.value) return false;
  if (!normalizedCatApiBase.value) return false;
  return normalizedCatApiBase.value !== savedSelectedCatApiBase.value;
});

const catSyncFromServerOptions = computed(() => {
  const options = [{ key: '', label: '请选择' }];
  if (catServerAddMode.value) {
    catServers.value
      .filter((server) => server.name)
      .forEach((server) => {
        options.push({
          key: server.name,
          label: server.name
        });
      });
    return options;
  }
  if (!catApiBaseChanged.value) return [];
  if (savedSelectedCatApiBase.value) {
    options.push({ key: '__current__', label: '当前服务器' });
  }
  catServers.value
    .filter((server) => server.name && server.name !== catSelectedServerKey.value)
    .forEach((server) => {
      options.push({
        key: server.name,
        label: server.name
      });
    });
  return options;
});

const selectedCatSyncFromServerLabel = computed(() => {
  if (!catSyncFromServerOptions.value.length) return '请选择';
  const matched = catSyncFromServerOptions.value.find((item) => item.key === catSyncFromServerKey.value);
  return matched ? matched.label : '请选择';
});

const showCatSyncFromServerRow = computed(() => {
  if (!catSyncFromServerOptions.value.length) return false;
  return catServerAddMode.value || catApiBaseChanged.value;
});

const showCatSettingsExtras = computed(() => {
  if (catServerAddMode.value) return false;
  if (!catSelectedServerKey.value) return false;
  return !catApiBaseChanged.value;
});

const canSaveCatServer = computed(() => {
  return !!catForm.value.name.trim() && !!normalizedCatApiBase.value;
});

const canSaveGoProxyEditor = computed(() => {
  return !!goProxyEditorForm.value.name.trim() && !!normalizeHttpBase(goProxyEditorForm.value.base);
});

const canSaveRelayEditor = computed(() => {
  return !!relayEditorForm.value.name.trim() && !!normalizeHttpBase(relayEditorForm.value.base);
});

const canSaveCatConfigEditor = computed(() => {
  return !!catConfigEditorForm.value.name.trim() && !!catConfigEditorForm.value.url.trim();
});

let nextToastId = 1;
let lastToastKey = '';
let lastToastAt = 0;

function toastIconSvg(type) {
  if (type === 'success') {
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M20 6 9 17l-5-5"/></svg>';
  }
  if (type === 'error') {
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M18 6 6 18"/><path d="M6 6l12 12"/></svg>';
  }
  return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 16v-4"/><path d="M12 8h.01"/><circle cx="12" cy="12" r="10"/></svg>';
}

function removeToast(id) {
  toasts.value = toasts.value.filter((toast) => toast.id !== id);
}

function pushToast(type, message, durationMs = 2600) {
  const msg = typeof message === 'string' ? message.trim() : '';
  if (!msg) return;
  const key = `${type}:${msg}`;
  const now = Date.now();
  if (key === lastToastKey && now - lastToastAt < 600) return;
  lastToastKey = key;
  lastToastAt = now;
  const id = nextToastId++;
  toasts.value = [...toasts.value, { id, type, message: msg }];
  const delay = Number.isFinite(Number(durationMs)) ? Math.max(600, Math.trunc(Number(durationMs))) : 2600;
  window.setTimeout(() => removeToast(id), delay);
}

function notifySuccess(message, durationMs) {
  pushToast('success', message, durationMs);
}

function notifyError(message, durationMs) {
  pushToast('error', message, durationMs);
}

function notifyInfo(message, durationMs) {
  pushToast('info', message, durationMs);
}

function setMagicTestOutput(targetRef, type, text) {
  targetRef.value = {
    type: type || '',
    text: typeof text === 'string' ? text : ''
  };
}

function normalizeRegexText(text) {
  const raw = typeof text === 'string' ? text : '';
  if (!raw) return '';
  return raw.replace(/\\\\(?=[dDsSwWbB.()[\]{}+*?^$|\\\-_/])/g, '\\');
}

function normalizePatternInput(text) {
  const raw = typeof text === 'string' ? text.trim() : '';
  if (!raw) return null;
  if (raw.startsWith('/') && raw.lastIndexOf('/') > 0) {
    const last = raw.lastIndexOf('/');
    const pattern = normalizeRegexText(raw.slice(1, last).trim());
    const flags = raw.slice(last + 1).trim();
    if (!pattern) return null;
    return { pattern, flags };
  }
  return { pattern: normalizeRegexText(raw), flags: '' };
}

function decodeEpisodeRule(rule) {
  const raw = typeof rule === 'string' ? rule.trim() : '';
  if (!raw) return null;
  if (raw.startsWith('{') && raw.endsWith('}')) {
    try {
      const obj = JSON.parse(raw);
      if (obj && typeof obj === 'object' && typeof obj.pattern === 'string' && obj.pattern.trim()) {
        return {
          pattern: normalizeRegexText(String(obj.pattern || '').trim()),
          replace: typeof obj.replace === 'string' ? obj.replace : '',
          flags: typeof obj.flags === 'string' ? obj.flags : ''
        };
      }
    } catch (_e) {}
  }
  if (raw.startsWith('/') && raw.lastIndexOf('/') > 0) {
    const last = raw.lastIndexOf('/');
    return {
      pattern: normalizeRegexText(raw.slice(1, last).trim()),
      replace: '',
      flags: raw.slice(last + 1).trim()
    };
  }
  return { pattern: normalizeRegexText(raw), replace: '', flags: '' };
}

function encodeEpisodeRule(rule) {
  const pattern = normalizeRegexText(rule && typeof rule.pattern === 'string' ? rule.pattern.trim() : '');
  if (!pattern) return '';
  const replace = rule && typeof rule.replace === 'string' ? rule.replace : '';
  const flags = rule && typeof rule.flags === 'string' ? rule.flags.trim() : '';
  const payload = { pattern, replace: replace || '' };
  if (flags) payload.flags = flags;
  try {
    return JSON.stringify(payload);
  } catch (_e) {
    return '';
  }
}

function normalizeAggregateRegexRuleInput(text) {
  const parsed = normalizePatternInput(text);
  if (!parsed || !parsed.pattern) return '';
  return parsed.flags ? `/${parsed.pattern}/${parsed.flags}` : parsed.pattern;
}

function buildRegexFromInput(raw, { defaultFlags = '', forceGlobal = false } = {}) {
  const parsed = normalizePatternInput(raw);
  if (!parsed || !parsed.pattern) return null;
  let flags = parsed.flags || defaultFlags || '';
  if (forceGlobal && !flags.includes('g')) flags += 'g';
  try {
    return new RegExp(parsed.pattern, flags);
  } catch (_e) {
    return null;
  }
}

function normalizeReplaceTemplate(replaceRaw) {
  const raw = typeof replaceRaw === 'string' ? replaceRaw : '';
  return raw ? raw.replace(/\\(\d+)/g, '$$$1') : '';
}

function buildDefaultSmartSourceRuleRows() {
  return [
    { key: 'quality', label: '画质', order: 1 },
    { key: 'pan', label: '网盘', order: 2 },
    { key: 'keyword', label: '关键字', order: 3 }
  ];
}

function cloneSmartSourceRuleRows(rows) {
  return (Array.isArray(rows) ? rows : []).map((row, index) => ({
    key: row && typeof row.key === 'string' ? row.key : `row-${index}`,
    label: row && typeof row.label === 'string' ? row.label : '',
    order: index + 1
  }));
}

function normalizeSmartSourceRuleRowsFromPayload(rows) {
  const source = Array.isArray(rows) ? rows : [];
  const seen = new Set();
  const mapped = source
    .map((row) => ({
      key: row && typeof row.key === 'string' ? row.key.trim().toLowerCase() : '',
      order: Number.isFinite(Number(row && row.order)) ? Math.trunc(Number(row.order)) : 0
    }))
    .filter((row) => row.key === 'quality' || row.key === 'keyword' || row.key === 'pan')
    .sort((a, b) => a.order - b.order);
  const ordered = [];
  mapped.forEach((row) => {
    if (seen.has(row.key)) return;
    seen.add(row.key);
    ordered.push(row);
  });
  ['quality', 'pan', 'keyword'].forEach((key) => {
    if (!seen.has(key)) ordered.push({ key, order: ordered.length + 1 });
  });
  return cloneSmartSourceRuleRows(
    ordered.map((row) => ({
      key: row.key,
      label: row.key === 'quality' ? '画质' : row.key === 'keyword' ? '关键字' : '网盘',
      order: row.order
    }))
  );
}

function moveSmartSourceRuleRow(index, direction) {
  const from = Number.isInteger(index) ? index : -1;
  if (from < 0 || from >= smartSourceRuleRows.value.length) return;
  const to = direction === 'up' ? from - 1 : direction === 'down' ? from + 1 : from;
  if (to < 0 || to >= smartSourceRuleRows.value.length || to === from) return;
  const next = smartSourceRuleRows.value.slice();
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);
  smartSourceRuleRows.value = cloneSmartSourceRuleRows(next);
}

function stripDisplaySequencePrefix(text) {
  const raw = typeof text === 'string' ? text : String(text || '');
  return raw.replace(/^\s*\d+\s*[.．、)）]\s+/u, '').trim();
}

function normalizeCommaTokenLine(text) {
  const raw = typeof text === 'string' ? text : String(text || '');
  return raw
    .replaceAll('，', ',')
    .split(',')
    .map((item) => String(item || '').trim())
    .filter(Boolean)
    .filter((item, index, list) => list.findIndex((value) => value.toLowerCase() === item.toLowerCase()) === index);
}

function normalizeSmartPanAliasMappings(list) {
  const rows = Array.isArray(list) ? list : [];
  const seen = new Set();
  const out = [];
  rows.forEach((row) => {
    const pan = stripDisplaySequencePrefix(row && row.pan != null ? String(row.pan) : '');
    if (!pan) return;
    const key = pan.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    out.push({
      pan,
      aliases: normalizeCommaTokenLine(row && row.aliases != null ? String(row.aliases) : '').join(',')
    });
  });
  return out;
}

function applySmartSettings(data) {
  const root = data && typeof data === 'object' ? data : {};
  const sourcePriorityTokens = normalizeCommaTokenLine(root.smartSourcePriorityTokens || '');
  const panMatchTokens = normalizeCommaTokenLine(root.smartPanMatchTokens || '');
  const aliasMappings = normalizeSmartPanAliasMappings(Array.isArray(root.smartPanAliasMappings) ? root.smartPanAliasMappings : []);
  smartSourceRuleRows.value = normalizeSmartSourceRuleRowsFromPayload(root.smartSourceRuleRows);
  smartSourcePriorityTokensInput.value = sourcePriorityTokens.join(',');
  smartPanMatchTokensInput.value = panMatchTokens.join(',');
  smartSiteCleanKeywordsInput.value = normalizeCommaTokenLine(root.siteCleanKeywords || '').join(',');
  smartPanAliasMappings.value = aliasMappings;
  magicShadowSmartSettings.value = {
    smartSourcePriorityTokens: sourcePriorityTokens,
    smartPanMatchTokens: panMatchTokens,
    smartPanAliasMappings: aliasMappings.map((item) => ({ ...item })),
    smartSourceRuleRows: cloneSmartSourceRuleRows(smartSourceRuleRows.value)
  };
}

function buildSmartSavePayload() {
  return {
    smartSourceRuleRows: cloneSmartSourceRuleRows(smartSourceRuleRows.value).map((row) => ({
      key: row.key,
      order: row.order
    })),
    siteCleanKeywords: smartSiteCleanKeywordTokens.value.join(','),
    smartSourcePriorityTokens: normalizeCommaTokenLine(smartSourcePriorityTokensInput.value),
    smartPanMatchTokens: normalizeCommaTokenLine(smartPanMatchTokensInput.value),
    smartPanAliasMappings: normalizeSmartPanAliasMappings(smartPanAliasMappings.value)
  };
}

function normalizeMagicRuleRow(rule) {
  return {
    pattern: normalizeRegexText(rule && typeof rule.pattern === 'string' ? rule.pattern.trim() : ''),
    replace: rule && typeof rule.replace === 'string' ? rule.replace : '',
    flags: rule && typeof rule.flags === 'string' ? rule.flags.trim() : ''
  };
}

function normalizeUserRow(user) {
  return {
    username: user && typeof user.username === 'string' ? user.username : '',
    role: user && typeof user.role === 'string' ? user.role : 'user',
    status: user && typeof user.status === 'string' ? user.status : 'active'
  };
}

function isThirdPartyModuleRequiringSite(module) {
  return String(module || '').trim().toLowerCase() === 'site_data';
}

function normalizeThirdPartyHomeSection(section) {
  const row = section && typeof section === 'object' ? section : {};
  const id = typeof row.id === 'string' ? row.id.trim() : '';
  const name = typeof row.name === 'string' ? row.name.trim() : '';
  if (!name) return null;
  const moduleAllowed = new Set(EMBY_MODULE_OPTIONS.map((item) => item.value));
  const module = moduleAllowed.has(String(row.module || '').trim().toLowerCase())
    ? String(row.module || '').trim().toLowerCase()
    : 'douban_tv';
  let mediaType = String(row.mediaType || '').trim().toLowerCase();
  if (mediaType !== 'tv' && mediaType !== 'movie') {
    mediaType = module === 'douban_movie' ? 'movie' : 'tv';
  }
  const generatedId = `view_custom_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
  const finalId = (id || generatedId).startsWith('view_') ? (id || generatedId) : `view_${id || generatedId}`;
  const siteKey = typeof row.siteKey === 'string' ? row.siteKey.trim() : '';
  const categoryId = typeof row.categoryId === 'string' ? row.categoryId.trim() : '';
  const cardStyleRaw = typeof row.cardStyle === 'string' ? row.cardStyle.trim().toLowerCase() : '';
  const out = { id: finalId, name, module, mediaType, siteKey: '', categoryId: '', cardStyle: '' };
  if (isThirdPartyModuleRequiringSite(module)) {
    out.siteKey = siteKey;
    out.categoryId = categoryId;
    out.cardStyle = cardStyleRaw === 'site' || cardStyleRaw === 'tmdb' ? cardStyleRaw : 'tmdb';
  }
  return out;
}

function validateThirdPartyHomeSections(list) {
  const rows = Array.isArray(list) ? list : [];
  for (let index = 0; index < rows.length; index += 1) {
    const row = rows[index] && typeof rows[index] === 'object' ? rows[index] : {};
    const name = typeof row.name === 'string' ? row.name.trim() : '';
    const module = typeof row.module === 'string' ? row.module.trim().toLowerCase() : '';
    const mediaType = typeof row.mediaType === 'string' ? row.mediaType.trim().toLowerCase() : '';
    if (!name) return `第 ${index + 1} 行：显示名称不能为空`;
    if (!module) return `第 ${index + 1} 行：主模块不能为空`;
    if (mediaType !== 'tv' && mediaType !== 'movie') return `第 ${index + 1} 行：栏目类型必须是 电视剧/电影`;
    if (isThirdPartyModuleRequiringSite(module)) {
      const siteKey = typeof row.siteKey === 'string' ? row.siteKey.trim() : '';
      const categoryId = typeof row.categoryId === 'string' ? row.categoryId.trim() : '';
      const cardStyle = typeof row.cardStyle === 'string' ? row.cardStyle.trim().toLowerCase() : '';
      if (!siteKey) return `第 ${index + 1} 行：请选择站点`;
      if (!categoryId) return `第 ${index + 1} 行：请选择分类`;
      if (cardStyle !== 'tmdb' && cardStyle !== 'site') return `第 ${index + 1} 行：请选择卡片属性`;
    }
  }
  return '';
}

function buildThirdPartyHomeSectionId() {
  return `view_custom_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}

function closeThirdPartyDropdown() {
  thirdPartyDropdownOpenKey.value = '';
}

function getThirdPartyOptionLabel(options, value, fallback = '请选择') {
  const list = Array.isArray(options) ? options : [];
  const key = typeof value === 'string' ? value.trim() : '';
  const matched = list.find((item) => item && item.value === key);
  if (matched && typeof matched.label === 'string' && matched.label.trim()) return matched.label;
  const matchedKey = list.find((item) => item && item.key === key);
  if (matchedKey && typeof matchedKey.label === 'string' && matchedKey.label.trim()) return matchedKey.label;
  return fallback;
}

function getThirdPartyCategories(siteKey) {
  const key = typeof siteKey === 'string' ? siteKey.trim() : '';
  const map = thirdPartySiteCategories.value && typeof thirdPartySiteCategories.value === 'object'
    ? thirdPartySiteCategories.value
    : {};
  return Array.isArray(map[key]) ? map[key] : [];
}

function getThirdPartyCategoryLabel(siteKey, categoryId) {
  const key = typeof categoryId === 'string' ? categoryId.trim() : '';
  if (!key) return '选择分类';
  const matched = getThirdPartyCategories(siteKey).find((item) => item && item.id === key);
  return matched && matched.name ? matched.name : '选择分类';
}

function applyThirdPartyHomeSections(list) {
  const rows = Array.isArray(list)
    ? list.map(normalizeThirdPartyHomeSection).filter(Boolean)
    : [];
  thirdPartyHomeSections.value = rows.length
    ? rows
    : DEFAULT_EMBY_HOME_SECTIONS.map(normalizeThirdPartyHomeSection).filter(Boolean);
}

async function loadThirdPartyHomeSites() {
  try {
    const data = await fetchDashboardVideoSourceSites();
    const sites = Array.isArray(data && data.sites) ? data.sites : [];
    thirdPartyHomeSites.value = sites
      .filter((site) => site && site.home && site.enabled && site.key && site.name && site.api)
      .map((site) => ({
        key: String(site.key),
        value: String(site.key),
        label: String(site.name),
        api: String(site.api || '')
      }));
  } catch (_e) {
    thirdPartyHomeSites.value = [];
  }
}

async function ensureThirdPartySiteCategoriesLoaded(siteKey) {
  const key = typeof siteKey === 'string' ? siteKey.trim() : '';
  if (!key) return [];
  if (Array.isArray(thirdPartySiteCategories.value[key])) {
    return thirdPartySiteCategories.value[key];
  }
  try {
    const data = await fetchThirdpartySiteCategories(key);
    const categories = Array.isArray(data && data.categories)
      ? data.categories
          .map((item) => ({
            id: item && typeof item.id === 'string' ? item.id.trim() : '',
            name: item && typeof item.name === 'string' ? item.name.trim() : ''
          }))
          .filter((item) => item.id && item.name)
          .slice(0, 200)
      : [];
    thirdPartySiteCategories.value = {
      ...thirdPartySiteCategories.value,
      [key]: categories
    };
    return categories;
  } catch (_e) {
    thirdPartySiteCategories.value = {
      ...thirdPartySiteCategories.value,
      [key]: []
    };
    return [];
  }
}

function toggleThirdPartyDropdown(index, field) {
  const next = `${index}:${field}`;
  thirdPartyDropdownOpenKey.value = thirdPartyDropdownOpenKey.value === next ? '' : next;
}

function updateThirdPartyHomeSectionField(index, field, value, closeAfter = false) {
  const list = Array.isArray(thirdPartyHomeSections.value) ? thirdPartyHomeSections.value.slice() : [];
  if (!list[index]) return;
  list[index] = normalizeThirdPartyHomeSection({
    ...list[index],
    [field]: value
  });
  thirdPartyHomeSections.value = list.filter(Boolean);
  if (closeAfter) closeThirdPartyDropdown();
}

async function selectThirdPartyModule(index, value) {
  const list = Array.isArray(thirdPartyHomeSections.value) ? thirdPartyHomeSections.value.slice() : [];
  const row = list[index];
  if (!row) return;
  const nextModule = String(value || '').trim().toLowerCase();
  const next = {
    ...row,
    module: nextModule
  };
  if (!isThirdPartyModuleRequiringSite(nextModule)) {
    next.siteKey = '';
    next.categoryId = '';
    next.cardStyle = '';
  } else if (!next.cardStyle) {
    next.cardStyle = 'tmdb';
  }
  if (!next.mediaType) {
    next.mediaType = nextModule === 'douban_movie' ? 'movie' : 'tv';
  }
  list[index] = normalizeThirdPartyHomeSection(next);
  thirdPartyHomeSections.value = list.filter(Boolean);
  closeThirdPartyDropdown();
}

async function selectThirdPartySite(index, siteKey) {
  const list = Array.isArray(thirdPartyHomeSections.value) ? thirdPartyHomeSections.value.slice() : [];
  const row = list[index];
  if (!row) return;
  const nextSiteKey = typeof siteKey === 'string' ? siteKey.trim() : '';
  list[index] = normalizeThirdPartyHomeSection({
    ...row,
    siteKey: nextSiteKey,
    categoryId: ''
  });
  thirdPartyHomeSections.value = list.filter(Boolean);
  closeThirdPartyDropdown();
  if (!nextSiteKey) return;
  const categories = await ensureThirdPartySiteCategoriesLoaded(nextSiteKey);
  const current = Array.isArray(thirdPartyHomeSections.value) ? thirdPartyHomeSections.value.slice() : [];
  const currentRow = current[index];
  if (!currentRow || currentRow.siteKey !== nextSiteKey || currentRow.categoryId || !categories.length) return;
  current[index] = normalizeThirdPartyHomeSection({
    ...currentRow,
    categoryId: categories[0].id
  });
  thirdPartyHomeSections.value = current.filter(Boolean);
}

function selectThirdPartyCategory(index, categoryId) {
  updateThirdPartyHomeSectionField(index, 'categoryId', categoryId, true);
}

function addThirdPartyHomeSection() {
  const next = normalizeThirdPartyHomeSection({
    id: buildThirdPartyHomeSectionId(),
    name: '新栏目',
    module: 'douban_tv',
    mediaType: 'tv',
    siteKey: '',
    categoryId: '',
    cardStyle: ''
  });
  if (!next) return;
  thirdPartyHomeSections.value = thirdPartyHomeSections.value.concat([next]);
  closeThirdPartyDropdown();
}

function restoreThirdPartyHomeSectionDefaults() {
  applyThirdPartyHomeSections(DEFAULT_EMBY_HOME_SECTIONS);
  closeThirdPartyDropdown();
}

function moveThirdPartyHomeSection(index, delta) {
  const list = Array.isArray(thirdPartyHomeSections.value) ? thirdPartyHomeSections.value.slice() : [];
  const target = index + delta;
  if (!list[index] || target < 0 || target >= list.length) return;
  const temp = list[target];
  list[target] = list[index];
  list[index] = temp;
  thirdPartyHomeSections.value = list;
  closeThirdPartyDropdown();
}

function removeThirdPartyHomeSection(index) {
  const list = Array.isArray(thirdPartyHomeSections.value) ? thirdPartyHomeSections.value.slice() : [];
  list.splice(index, 1);
  thirdPartyHomeSections.value = list;
  closeThirdPartyDropdown();
}

function closeSearchDisplayDropdown() {
  searchDisplayDropdownOpen.value = false;
}

function closeDoubanDataDropdown() {
  doubanDataDropdownOpen.value = false;
}

function closeDoubanImgDropdown() {
  doubanImgDropdownOpen.value = false;
}

function closeCatServerDropdown() {
  catServerDropdownOpen.value = false;
}

function closeCatSyncFromServerDropdown() {
  catSyncFromServerDropdownOpen.value = false;
}

function supportsQrLogin(key) {
  return key === 'baidu' || key === 'quark' || key === 'uc' || key === '115' || key === 'bili';
}

function isCookiePan(def) {
  return !!def && def.type === 'cookie';
}

function isAuthorizationPan(def) {
  return !!def && def.type === 'authorization';
}

function isTvPan(def) {
  return !!def && (def.type === 'quark_tv' || def.type === 'uc_tv');
}

function shouldUseFullWidthPanInputs(def) {
  if (!def || typeof def.key !== 'string') return false;
  return ['quark_tv', '189', 'uc_tv', 'pan123', 'yunchao'].includes(def.key);
}

function patchPanSetting(key, patch) {
  if (!key) return;
  const current = panSettingsStore.value && typeof panSettingsStore.value[key] === 'object' ? panSettingsStore.value[key] : {};
  panSettingsStore.value = {
    ...panSettingsStore.value,
    [key]: {
      ...current,
      ...(patch || {})
    }
  };
}

function patchActivePanSetting(patch) {
  patchPanSetting(activePanSettingKey.value, patch);
}

async function persistActivePanSettings(payload) {
  if (!activePanSettingDef.value || panSaveBusy.value) return null;
  const key = activePanSettingDef.value.key;
  panSaveBusy.value = true;
  try {
    const data = await savePanLoginSettings(payload);
    const settings = data && data.settings && typeof data.settings === 'object' ? data.settings : {};
    panSettingsStore.value = { ...panSettingsStore.value, ...settings };
    loadedPanSettingKeys.add(key);
    notifySuccess('保存成功');
    return data;
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
    return null;
  } finally {
    panSaveBusy.value = false;
  }
}

async function ensurePanSettingLoaded(key) {
  const targetKey = typeof key === 'string' ? key.trim() : '';
  if (!targetKey || loadedPanSettingKeys.has(targetKey)) return;
  panLoading.value = true;
  try {
    const data = await fetchPanSettings(targetKey);
    const settings = data && data.settings && typeof data.settings === 'object' ? data.settings : {};
    panSettingsStore.value = {
      ...panSettingsStore.value,
      ...settings
    };
    loadedPanSettingKeys.add(targetKey);
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    panLoading.value = false;
  }
}

async function loadPanPanel() {
  if (!isAdmin.value) return;
  const firstKey = activePanSettingKey.value || (panSettingDefs[0] && panSettingDefs[0].key) || '';
  if (firstKey) {
    await ensurePanSettingLoaded(firstKey);
  }
}

async function selectPanSetting(key) {
  const nextKey = typeof key === 'string' ? key.trim() : '';
  if (!nextKey || nextKey === activePanSettingKey.value) return;
  stopAllQrPolling();
  activePanSettingKey.value = nextKey;
  await ensurePanSettingLoaded(nextKey);
}

async function saveActivePanText() {
  if (!activePanSettingDef.value) return;
  const key = activePanSettingDef.value.key;
  const type = activePanSettingDef.value.type === 'authorization' ? 'authorization' : 'cookie';
  const payload = {
    key,
    type
  };
  if (type === 'authorization') payload.authorization = activePanTextValue.value;
  else payload.cookie = activePanTextValue.value;
  await persistActivePanSettings(payload);
}

async function saveActivePanAccount() {
  if (!activePanSettingDef.value) return;
  const key = activePanSettingDef.value.key;
  const payload = {
    key,
    type: 'account',
    username: activePanAccount.value.username,
    password: activePanAccount.value.password,
    cookie: key === '189' ? activePanAccount.value.cookie : ''
  };
  await persistActivePanSettings(payload);
}

async function saveActivePanTv() {
  if (!activePanSettingDef.value) return;
  const key = activePanSettingDef.value.key;
  const payload = {
    key,
    type: activePanSettingDef.value.type,
    refresh_token: activePanTv.value.refresh_token,
    device_id: activePanTv.value.device_id,
    access_token: activePanTv.value.access_token,
    access_token_exp_at: activePanTv.value.access_token_exp_at
  };
  await persistActivePanSettings(payload);
}

function setQrState(provider, patch) {
  if (!provider) return;
  panQrStates.value = {
    ...panQrStates.value,
    [provider]: {
      ...(panQrStates.value[provider] || { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' }),
      ...(patch || {})
    }
  };
}

function clearQrState(provider) {
  setQrState(provider, { qid: '', expiresAt: 0, imageUrl: '', polling: false, phase: 'idle' });
}

const qrPollTimers = new Map();

function clearQrPollingTimer(provider) {
  const timer = qrPollTimers.get(provider);
  if (timer) {
    clearInterval(timer);
    qrPollTimers.delete(provider);
  }
}

function stopQrPolling(provider) {
  clearQrPollingTimer(provider);
  setQrState(provider, { polling: false });
}

function stopAllQrPolling() {
  Array.from(qrPollTimers.keys()).forEach((provider) => stopQrPolling(provider));
}

function isQrExpired(provider) {
  const state = panQrStates.value[provider];
  if (!state || !state.qid) return true;
  const expiresAt = Number(state.expiresAt || 0);
  if (!Number.isFinite(expiresAt) || expiresAt <= 0) return false;
  return Date.now() > expiresAt - 1500;
}

async function startActivePanQrLogin() {
  const provider = activePanSettingKey.value;
  if (!supportsQrLogin(provider) || activeQrButtonBusy.value) return;
  stopQrPolling(provider);
  clearQrState(provider);
  setQrState(provider, { phase: 'starting' });
  try {
    const data = await startPanQrLogin(provider);
    setQrState(provider, {
      qid: data && data.qid ? String(data.qid) : '',
      expiresAt: Number(data && data.expiresAt ? data.expiresAt : 0),
      imageUrl: data && data.imageUrl ? String(data.imageUrl) : '',
      polling: true,
      phase: 'pending'
    });
    startQrPolling(provider);
  } catch (err) {
    clearQrState(provider);
    notifyError((err && err.message) || '生成失败');
  }
}

function startQrPolling(provider) {
  clearQrPollingTimer(provider);
  setQrState(provider, { polling: true });
  const tick = async () => {
    const state = panQrStates.value[provider];
    if (!state || !state.qid || isQrExpired(provider)) {
      stopQrPolling(provider);
      clearQrState(provider);
      notifyError('二维码登录超时/已过期');
      return;
    }
    try {
      const { resp, data } = await pollPanQrLogin(provider, state.qid);
      if (resp && resp.ok && data && data.success === true && typeof data.cookie === 'string') {
        patchPanSetting(provider, { cookie: String(data.cookie || '') });
        loadedPanSettingKeys.add(provider);
        stopQrPolling(provider);
        clearQrState(provider);
        notifySuccess('Cookie已获取并自动保存成功');
        return;
      }
      if (resp && resp.status === 409) {
        const status = data && data.status ? String(data.status) : 'pending';
        const phase = status === 'scanned' ? 'scanned' : 'pending';
        setQrState(provider, { phase });
        return;
      }
      stopQrPolling(provider);
      clearQrState(provider);
      notifyError((data && data.message) || `HTTP ${resp ? resp.status : 500}`);
    } catch (err) {
      stopQrPolling(provider);
      clearQrState(provider);
      notifyError((err && err.message) || '获取失败');
    }
  };
  tick();
  qrPollTimers.set(provider, setInterval(tick, 1500));
}

function toggleSearchDisplayDropdown() {
  searchDisplayDropdownOpen.value = !searchDisplayDropdownOpen.value;
}

function toggleDoubanDataDropdown() {
  if (metadataLoading.value || metadataSaving.value) return;
  doubanDataDropdownOpen.value = !doubanDataDropdownOpen.value;
}

function selectDoubanDataProxy(value) {
  metadataForm.value.doubanDataProxy = typeof value === 'string' ? value : 'server-proxy';
  closeDoubanDataDropdown();
}

function toggleDoubanImgDropdown() {
  if (metadataLoading.value || metadataSaving.value) return;
  doubanImgDropdownOpen.value = !doubanImgDropdownOpen.value;
}

function selectDoubanImgProxy(value) {
  metadataForm.value.doubanImgProxy = typeof value === 'string' ? value : 'server-proxy';
  closeDoubanImgDropdown();
}

async function selectSearchDisplayMode(value) {
  siteForm.value.searchDisplayMode = value;
  closeSearchDisplayDropdown();
  if (value === 'sites') {
    searchDisplayModeError.value = '';
    return;
  }
  const result = await validateSearchDisplayMode(value);
  if (!result.valid) {
    siteForm.value.searchDisplayMode = 'sites';
    searchDisplayModeError.value = result.message || 'TMDB API TOKEN 未设置';
    return;
  }
  searchDisplayModeError.value = '';
}

function onDocumentClick(event) {
  const target = event && event.target ? event.target : null;
  const searchRoot = searchDisplayDropdownRef.value;
  if (searchRoot && !(target && searchRoot.contains(target))) {
    closeSearchDisplayDropdown();
  }
  const doubanDataRoot = doubanDataDropdownRef.value;
  if (doubanDataRoot && !(target && doubanDataRoot.contains(target))) {
    closeDoubanDataDropdown();
  }
  const doubanImgRoot = doubanImgDropdownRef.value;
  if (doubanImgRoot && !(target && doubanImgRoot.contains(target))) {
    closeDoubanImgDropdown();
  }
  const catRoot = catServerDropdownRef.value;
  if (catRoot && !(target && catRoot.contains(target))) {
    closeCatServerDropdown();
  }
  const catSyncRoot = catSyncFromServerDropdownRef.value;
  if (catSyncRoot && !(target && catSyncRoot.contains(target))) {
    closeCatSyncFromServerDropdown();
  }
  const inThirdPartySelectbox = !!(target && typeof target.closest === 'function' && target.closest('.thirdparty-selectbox'));
  if (!inThirdPartySelectbox) {
    closeThirdPartyDropdown();
  }
}

function normalizeCatServerRow(server) {
  const row = server && typeof server === 'object' ? server : {};
  return {
    name: typeof row.name === 'string' ? row.name : '',
    apiBase: typeof row.apiBase === 'string' ? row.apiBase : ''
  };
}

function normalizeGoProxyPanMap(pans) {
  const source = pans && typeof pans === 'object' ? pans : {};
  return {
    baidu: source.baidu !== false,
    quark: source.quark !== false
  };
}

function normalizeCatConfigRow(item) {
  const row = item && typeof item === 'object' ? item : {};
  const statusRaw = typeof row.status === 'string' ? row.status.trim().toLowerCase() : '';
  const updateResultRaw = typeof row.updateResult === 'string' ? row.updateResult.trim().toLowerCase() : '';
  const checkedAtRaw = Number.isFinite(Number(row.checkedAt)) ? Math.trunc(Number(row.checkedAt)) : 0;
  const updateAtRaw = Number.isFinite(Number(row.updateAt)) ? Math.trunc(Number(row.updateAt)) : 0;
  return {
    name: typeof row.name === 'string' ? row.name.trim() : '',
    url: typeof row.url === 'string' ? row.url.trim() : '',
    id: typeof row.id === 'string' ? row.id : '',
    status:
      statusRaw === 'pass' || statusRaw === 'error' || statusRaw === 'unchecked' || statusRaw === 'checking'
        ? statusRaw
        : 'unchecked',
    checkedAt: checkedAtRaw > 0 ? checkedAtRaw : 0,
    updateAt: updateAtRaw > 0 ? updateAtRaw : 0,
    updateResult:
      updateResultRaw === 'pass' || updateResultRaw === 'error' || updateResultRaw === 'updating'
        ? updateResultRaw
        : ''
  };
}

function normalizeCatPanRow(item, index = 0) {
  const row = item && typeof item === 'object' ? item : {};
  return {
    key: typeof row.key === 'string' ? row.key : '',
    name: typeof row.name === 'string' ? row.name : '',
    enable: row.enable !== false,
    sort: index + 1
  };
}

function normalizeCatConfigStatus(value) {
  const raw = typeof value === 'string' ? value.trim() : '';
  if (raw === 'pass' || raw === 'error' || raw === 'unchecked' || raw === 'checking') return raw;
  return 'unchecked';
}

function normalizeCatConfigUpdateResult(value) {
  const raw = typeof value === 'string' ? value.trim().toLowerCase() : '';
  if (raw === 'pass' || raw === 'error' || raw === 'updating') return raw;
  return '';
}

function formatCatConfigStatusText(status) {
  const normalizedStatus = normalizeCatConfigStatus(status);
  if (normalizedStatus === 'pass') return '通过';
  if (normalizedStatus === 'checking') return '检测中';
  if (normalizedStatus === 'error') return '异常';
  return '未检测';
}

function catConfigStatusTagClass(status) {
  const normalizedStatus = normalizeCatConfigStatus(status);
  if (normalizedStatus === 'pass') return 'tag-green';
  if (normalizedStatus === 'error') return 'tag-yellow';
  if (normalizedStatus === 'checking') return 'tag-gray';
  return 'tag-gray';
}

function formatCatConfigUpdateResultText(updateResult) {
  const normalized = normalizeCatConfigUpdateResult(updateResult);
  if (normalized === 'pass') return '成功';
  if (normalized === 'error') return '失败';
  if (normalized === 'updating') return '更新中';
  return '-';
}

function catConfigUpdateResultTagClass(updateResult) {
  const normalized = normalizeCatConfigUpdateResult(updateResult);
  if (normalized === 'pass') return 'tag-green';
  if (normalized === 'error') return 'tag-red';
  return 'tag-gray';
}

function formatCatConfigTimestamp(ts) {
  const value = Number.isFinite(Number(ts)) ? Math.trunc(Number(ts)) : 0;
  if (value <= 0) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '-';
  const pad2 = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${pad2(date.getHours())}:${pad2(date.getMinutes())}:${pad2(date.getSeconds())}`;
}

const catPanNameCellStyle = computed(() => ({
  width: `${catPanNameWidthPx.value}px`,
  minWidth: '120px',
  maxWidth: `${catPanNameWidthPx.value}px`,
  flex: `0 0 ${catPanNameWidthPx.value}px`,
  display: 'inline-block',
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis'
}));

const catPanFixedCellStyle = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  minWidth: '72px',
  width: '72px',
  flex: '0 0 72px'
};

const catPanHeaderFixedCellStyle = {
  display: 'inline-block',
  textAlign: 'center',
  minWidth: '72px',
  width: '72px',
  flex: '0 0 72px'
};

const videoSourceNameCellStyle = computed(() => ({
  width: `${videoSourceNameWidthPx.value}px`,
  minWidth: '80px',
  maxWidth: `${videoSourceNameWidthPx.value}px`,
  flex: `${videoSourceNameWidthPx.value}px 0 0`,
  display: 'inline-block',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap'
}));

const videoSourceApiCellStyle = computed(() => ({
  width: `${videoSourceApiWidthPx.value}px`,
  minWidth: '90px',
  maxWidth: `${videoSourceApiWidthPx.value}px`,
  flex: `${videoSourceApiWidthPx.value}px 0 0`,
  display: 'inline-block',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap'
}));

const videoSourceFixed72CellStyle = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  width: '72px',
  minWidth: '72px',
  flex: '0 0 72px'
};

const videoSourceFixed96CellStyle = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  width: '96px',
  minWidth: '96px',
  flex: '0 0 96px'
};

const videoSourceErrorCellStyle = {
  display: 'inline-block',
  minWidth: '240px',
  flex: '1 1 240px',
  whiteSpace: 'normal',
  overflowWrap: 'anywhere',
  wordBreak: 'break-word'
};

const allVideoSourceSelected = computed(() => {
  const sites = Array.isArray(videoSourceSites.value) ? videoSourceSites.value : [];
  if (!sites.length) return false;
  const enabled = new Set(selectedVideoSourceKeys.value);
  return sites.every((site) => site && site.key && enabled.has(site.key));
});

const hasSelectedVideoSources = computed(() => selectedVideoSourceKeys.value.length > 0);

function measureCatPanNameWidth(list) {
  if (typeof document === 'undefined') return 120;
  const measure = document.createElement('span');
  measure.className = 'adm-text-sm adm-font-medium';
  measure.style.position = 'absolute';
  measure.style.visibility = 'hidden';
  measure.style.whiteSpace = 'nowrap';
  measure.style.left = '-99999px';
  measure.style.top = '-99999px';
  document.body.appendChild(measure);
  let maxNamePx = 0;
  (Array.isArray(list) ? list : []).forEach((item) => {
    const text = (item && (item.name || item.key)) || '';
    measure.textContent = text;
    maxNamePx = Math.max(maxNamePx, Math.ceil(measure.getBoundingClientRect().width));
  });
  document.body.removeChild(measure);
  return Math.max(maxNamePx, 120);
}

function applyCatPans(list) {
  const normalized = (Array.isArray(list) ? list : []).map((item, index) => normalizeCatPanRow(item, index));
  catPanNameWidthPx.value = measureCatPanNameWidth(normalized);
  catPans.value = normalized;
}

function normalizeCatPansForSave() {
  return catPans.value.map((item, index) => ({
    key: item.key,
    name: item.name,
    enable: !!item.enable,
    sort: index + 1
  }));
}

function moveCatPan(index, direction) {
  const current = Array.isArray(catPans.value) ? [...catPans.value] : [];
  if (!current.length) return;
  const from = Number.isFinite(Number(index)) ? Math.trunc(Number(index)) : -1;
  if (from < 0 || from >= current.length) return;
  const to = direction === 'up' ? from - 1 : direction === 'down' ? from + 1 : from;
  if (to < 0 || to >= current.length || to === from) return;
  const [item] = current.splice(from, 1);
  current.splice(to, 0, item);
  catPans.value = current.map((row, idx) => ({
    ...row,
    sort: idx + 1
  }));
  catPanNameWidthPx.value = measureCatPanNameWidth(catPans.value);
}

function normalizeGoProxyServerRow(server) {
  const row = server && typeof server === 'object' ? server : {};
  return {
    name: typeof row.name === 'string' ? row.name : '',
    displayName: typeof row.displayName === 'string' ? row.displayName : '',
    base: normalizeHttpBase(row.base),
    pans: normalizeGoProxyPanMap(row.pans)
  };
}

function normalizeRelayServerRow(server) {
  const row = server && typeof server === 'object' ? server : {};
  return {
    name: typeof row.name === 'string' ? row.name : '',
    displayName: typeof row.displayName === 'string' ? row.displayName : '',
    base: normalizeHttpBase(row.base),
    secret: typeof row.secret === 'string' ? row.secret.trim() : ''
  };
}

function normalizeGoProxyProbeState(value) {
  const raw = typeof value === 'string' ? value.trim().toLowerCase() : '';
  if (raw === 'online' || raw === 'offline' || raw === 'checking') return raw;
  return 'checking';
}

function getGoProxyProbe(base) {
  const key = normalizeHttpBase(base).toLowerCase();
  if (!key) return { state: 'checking', version: '', checkedAt: 0 };
  const entry = goProxyProbes.value[key];
  return entry && typeof entry === 'object' ? entry : { state: 'checking', version: '', checkedAt: 0 };
}

function setGoProxyProbe(base, patch) {
  const key = normalizeHttpBase(base).toLowerCase();
  if (!key) return;
  goProxyProbes.value = {
    ...goProxyProbes.value,
    [key]: {
      state: 'checking',
      version: '',
      checkedAt: 0,
      ...(goProxyProbes.value[key] && typeof goProxyProbes.value[key] === 'object' ? goProxyProbes.value[key] : {}),
      ...(patch && typeof patch === 'object' ? patch : {})
    }
  };
}

function ensureGoProxyProbeEntry(base) {
  const key = normalizeHttpBase(base).toLowerCase();
  if (!key || goProxyProbes.value[key]) return;
  setGoProxyProbe(base, { state: 'checking', version: '', checkedAt: 0 });
}

function displayGoProxyBaseHost(base) {
  const raw = typeof base === 'string' ? base.trim() : '';
  if (!raw) return '';
  try {
    const url = new URL(raw);
    return String(url.host || '').trim() || raw;
  } catch (_e) {
    return raw;
  }
}

function goProxyProbeTagClass(state) {
  const normalized = normalizeGoProxyProbeState(state);
  if (normalized === 'online') return 'tag-green';
  if (normalized === 'offline') return 'tag-red';
  return 'tag-gray';
}

function goProxyProbeTextFor(state) {
  const normalized = normalizeGoProxyProbeState(state);
  if (normalized === 'online') return '在线';
  if (normalized === 'offline') return '离线';
  return '检测中';
}

function formatGoProxyVersion(base) {
  const probe = getGoProxyProbe(base);
  const state = normalizeGoProxyProbeState(probe.state);
  if (state === 'checking') return '检测中';
  if (state === 'online') return probe.version || '未知';
  return '异常';
}

function getRelayProbe(base) {
  const key = normalizeHttpBase(base).toLowerCase();
  if (!key) return { state: 'checking', version: '', checkedAt: 0 };
  const entry = relayProbes.value[key];
  return entry && typeof entry === 'object' ? entry : { state: 'checking', version: '', checkedAt: 0 };
}

function setRelayProbe(base, patch) {
  const key = normalizeHttpBase(base).toLowerCase();
  if (!key) return;
  relayProbes.value = {
    ...relayProbes.value,
    [key]: {
      state: 'checking',
      version: '',
      checkedAt: 0,
      ...(relayProbes.value[key] && typeof relayProbes.value[key] === 'object' ? relayProbes.value[key] : {}),
      ...(patch && typeof patch === 'object' ? patch : {})
    }
  };
}

function ensureRelayProbeEntry(base) {
  const key = normalizeHttpBase(base).toLowerCase();
  if (!key || relayProbes.value[key]) return;
  setRelayProbe(base, { state: 'checking', version: '', checkedAt: 0 });
}

function formatRelayVersion(base) {
  const probe = getRelayProbe(base);
  const state = normalizeGoProxyProbeState(probe.state);
  if (state === 'checking') return '检测中';
  if (state === 'online') return probe.version || '未知';
  return '异常';
}

function normalizeGoProxyServersJson(raw) {
  if (Array.isArray(raw)) return raw.map(normalizeGoProxyServerRow).filter((item) => item.base);
  if (typeof raw !== 'string' || !raw.trim()) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.map(normalizeGoProxyServerRow).filter((item) => item.base) : [];
  } catch (_e) {
    return [];
  }
}

function normalizeRelayServersJson(raw) {
  if (Array.isArray(raw)) return raw.map(normalizeRelayServerRow).filter((item) => item.base);
  if (typeof raw !== 'string' || !raw.trim()) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.map(normalizeRelayServerRow).filter((item) => item.base) : [];
  } catch (_e) {
    return [];
  }
}

function resetCatForm() {
  catForm.value = {
    name: '',
    apiBase: '',
    proxy: '',
    panBuiltinResolverEnabled: true,
    panMockEnabled: false,
    disableProxy: false,
    goProxyApi: ''
  };
}

async function reloadSelectedCatServerState() {
  if (catSelectedServerKey.value) {
    await hydrateSelectedCatServer();
    await loadCatPans();
  } else {
    resetCatForm();
    catPans.value = [];
  }
}

async function persistSelectedCatRemoteSettings(apiBase = normalizedCatApiBase.value) {
  const targetApiBase = normalizeHttpBase(apiBase);
  if (!targetApiBase) return;
  await saveCatpawrunnerAdminSettings(targetApiBase, buildCatRemoteSettingsPayload());
}

function resetCatConfigEditorForm() {
  catConfigEditorForm.value = {
    name: '',
    url: ''
  };
}

function closeCatConfigEditor() {
  catConfigEditorOpen.value = false;
  catConfigEditorMode.value = 'create';
  catConfigEditorIndex.value = -1;
  resetCatConfigEditorForm();
}

function openCatConfigEditorForCreate() {
  catConfigEditorMode.value = 'create';
  catConfigEditorIndex.value = -1;
  resetCatConfigEditorForm();
  catConfigEditorOpen.value = true;
}

function openCatConfigEditorForEdit(index) {
  const target = catOnlineConfigs.value[index];
  if (!target) return;
  catConfigEditorMode.value = 'edit';
  catConfigEditorIndex.value = index;
  catConfigEditorForm.value = normalizeCatConfigRow(target);
  catConfigEditorOpen.value = true;
}

function confirmCatConfigEditor() {
  if (!canSaveCatConfigEditor.value) return;
  const row = normalizeCatConfigRow(catConfigEditorForm.value);
  if (catConfigEditorMode.value === 'edit' && catConfigEditorIndex.value >= 0) {
    catOnlineConfigs.value = catOnlineConfigs.value.map((item, index) => (index === catConfigEditorIndex.value ? row : item));
  } else {
    catOnlineConfigs.value = [...catOnlineConfigs.value, row];
  }
  closeCatConfigEditor();
}

function removeCatConfig(index) {
  catOnlineConfigs.value = catOnlineConfigs.value.filter((_, currentIndex) => currentIndex !== index);
  if (catConfigEditorOpen.value && catConfigEditorMode.value === 'edit' && catConfigEditorIndex.value === index) {
    closeCatConfigEditor();
  }
}

function buildCatConfigActionKey(item, index) {
  const id = item && typeof item.id === 'string' ? item.id.trim() : '';
  if (id) return `id:${id}`;
  return `index:${Number.isFinite(Number(index)) ? Math.trunc(Number(index)) : -1}`;
}

async function triggerCatConfigUpdate(item, index) {
  if (catRemoteLoading.value || catSaving.value) return;
  const id = item && typeof item.id === 'string' ? item.id.trim() : '';
  if (!id) return;
  const actionKey = buildCatConfigActionKey(item, index);
  if (catConfigUpdateBusyKey.value === actionKey) return;
  catConfigUpdateBusyKey.value = actionKey;
  try {
    const data = await updateCatpawrunnerOnlineConfig(normalizedCatApiBase.value, id);
    applyCatRemoteSettings(data);
    notifySuccess('更新任务已提交');
  } catch (err) {
    notifyError((err && err.message) || '更新失败');
  } finally {
    catConfigUpdateBusyKey.value = '';
  }
}

function buildCatRemoteSettingsSnapshot(data) {
  const root = data && typeof data === 'object' ? data : {};
  const settings = root.settings && typeof root.settings === 'object' ? root.settings : {};
  const onlineConfigs = Array.isArray(root.onlineConfigs) ? root.onlineConfigs : [];
  return {
    proxy: typeof settings.proxy === 'string' ? settings.proxy : '',
    panBuiltinResolverEnabled: settings.panBuiltinResolverEnabled !== false,
    pan_mock: !!settings.pan_mock,
    disable_proxy: !!settings.disable_proxy,
    goProxyApi: typeof settings.goProxyApi === 'string' ? settings.goProxyApi : '',
    onlineConfigs: onlineConfigs.map(normalizeCatConfigRow).filter((item) => item.name || item.url)
  };
}

function applyCatRemoteSettingsSnapshot(snapshot) {
  const next = snapshot && typeof snapshot === 'object' ? snapshot : {};
  catForm.value.proxy = typeof next.proxy === 'string' ? next.proxy : '';
  catForm.value.panBuiltinResolverEnabled = next.panBuiltinResolverEnabled !== false;
  catForm.value.panMockEnabled = !!next.pan_mock;
  catForm.value.disableProxy = !!next.disable_proxy;
  catForm.value.goProxyApi = typeof next.goProxyApi === 'string' ? next.goProxyApi : '';
  catOnlineConfigs.value = Array.isArray(next.onlineConfigs)
    ? next.onlineConfigs.map(normalizeCatConfigRow).filter((item) => item.name || item.url)
    : [];
}

function applyCatRemoteSettings(data) {
  applyCatRemoteSettingsSnapshot(buildCatRemoteSettingsSnapshot(data));
}

function buildCatRemoteSettingsPayload() {
  return {
    proxy: catForm.value.proxy.trim(),
    panBuiltinResolverEnabled: !!catForm.value.panBuiltinResolverEnabled,
    pan_mock: !!catForm.value.panMockEnabled,
    disable_proxy: !!catForm.value.disableProxy,
    goProxyApi: catForm.value.goProxyApi.trim(),
    onlineConfigs: catOnlineConfigs.value.map((item) => ({
      id: typeof item.id === 'string' ? item.id : '',
      name: item.name,
      url: item.url
    }))
  };
}

function getRuntimeIdFromOnlineConfigsList(list) {
  const source = Array.isArray(list) ? list : [];
  for (const item of source) {
    const idRaw = item && typeof item.id === 'string' ? item.id.trim().toLowerCase() : '';
    if (/^[a-f0-9]{10}$/.test(idRaw)) return idRaw;
  }
  return '';
}

function getRuntimeIdFromOnlineConfigs() {
  return getRuntimeIdFromOnlineConfigsList(catOnlineConfigs.value);
}

function normalizeCatPansForRuntime(pans) {
  if (!Array.isArray(pans)) return [];
  return pans
    .map((p) => ({
      key: p && typeof p.key === 'string' ? p.key : '',
      name: p && typeof p.name === 'string' ? p.name : '',
      enable: !!(p && p.enable)
    }))
    .filter((p) => p.key);
}

async function loadCatPans({ apiBase = normalizedCatApiBase.value, onlineConfigs = catOnlineConfigs.value, silent = false } = {}) {
  const targetApiBase = normalizeHttpBase(apiBase);
  const runtimeId = getRuntimeIdFromOnlineConfigsList(onlineConfigs);
  if (targetApiBase && runtimeId) {
    try {
      const resp = await fetchCatpawrunnerWebsitePans(targetApiBase, runtimeId);
      const pans = normalizeCatPansForRuntime(unwrapCatpawrunnerWebsiteData(resp));
      try {
        await saveDashboardVideoPans(pans);
      } catch (_e) {}
      applyCatPans(pans);
      return;
    } catch (_e) {}
  }
  try {
    const panData = await fetchDashboardVideoPans();
    applyCatPans(panData.pans);
  } catch (err) {
    catPans.value = [];
    if (!silent) notifyError((err && err.message) || '读取网盘列表失败');
  }
}

async function hydrateSelectedCatServer({ silent = false } = {}) {
  const current = selectedCatServer.value;
  if (!current) {
    resetCatForm();
    return;
  }
  catForm.value.name = current.name || '';
  catForm.value.apiBase = current.apiBase || '';
  catForm.value.proxy = '';
  catForm.value.panBuiltinResolverEnabled = true;
  catForm.value.panMockEnabled = false;
  catForm.value.disableProxy = false;
  catForm.value.goProxyApi = '';
  if (!current.apiBase) return;
  catRemoteLoading.value = true;
  try {
    const data = await fetchCatpawrunnerAdminSettings(current.apiBase);
    applyCatRemoteSettings(data);
  } catch (err) {
    if (!silent) notifyError((err && err.message) || '读取 CatPawRunner 远程配置失败');
  } finally {
    catRemoteLoading.value = false;
  }
}

async function fetchCatRemoteSettingsSnapshot(apiBase) {
  const data = await fetchCatpawrunnerAdminSettings(apiBase);
  return buildCatRemoteSettingsSnapshot(data);
}

async function applyCatRemoteSettingsSnapshotToTarget(targetApiBase, snapshot) {
  const payload = {
    proxy: typeof snapshot.proxy === 'string' ? snapshot.proxy.trim() : '',
    panBuiltinResolverEnabled: snapshot.panBuiltinResolverEnabled !== false,
    pan_mock: !!snapshot.pan_mock,
    disable_proxy: !!snapshot.disable_proxy,
    goProxyApi: typeof snapshot.goProxyApi === 'string' ? snapshot.goProxyApi.trim() : '',
    onlineConfigs: Array.isArray(snapshot.onlineConfigs)
      ? snapshot.onlineConfigs.map((item) => {
          const row = normalizeCatConfigRow(item);
          return { name: row.name, url: row.url };
        })
      : []
  };
  return await saveCatpawrunnerAdminSettings(targetApiBase, payload);
}

async function syncCatSettingsBetweenServers({ sourceApiBase, targetApiBase }) {
  const snapshot = await fetchCatRemoteSettingsSnapshot(sourceApiBase);
  await applyCatRemoteSettingsSnapshotToTarget(targetApiBase, snapshot);
  return snapshot;
}

async function refreshCatServerAfterSave({ silent = true } = {}) {
  catSyncFromServerKey.value = '';
  if (catSelectedServerKey.value) {
    await hydrateSelectedCatServer({ silent });
    await loadCatPans({ silent });
    return;
  }
  resetCatForm();
  catPans.value = [];
}

async function loadInterfacePanel() {
  if (!isAdmin.value || interfaceLoading.value) return;
  interfaceLoading.value = true;
  try {
    const settings = await fetchSiteSettings();
    catServers.value = Array.isArray(settings.catpawrunnerServers)
      ? settings.catpawrunnerServers.map(normalizeCatServerRow).filter((item) => item.name)
      : [];
    catSelectedServerKey.value = typeof settings.CatpawrunnerActive === 'string' && settings.CatpawrunnerActive
      ? settings.CatpawrunnerActive
      : ((catServers.value[0] && catServers.value[0].name) || '');
    goProxyForm.value.enabled = !!settings.goProxyEnabled;
    goProxyForm.value.autoSelect = !!settings.goProxyAutoSelect;
    goProxyServers.value = normalizeGoProxyServersJson(settings.goProxyServersJson);
    await probeAllGoProxyServers();
    relayForm.value.enabled = !!settings.relayEnabled;
    relayForm.value.auth = typeof settings.auth === 'string' ? settings.auth : '';
    relayServers.value = normalizeRelayServersJson(settings.relayServersJson);
    await probeAllRelayServers();
    if (catSelectedServerKey.value) {
      await hydrateSelectedCatServer();
    } else {
      resetCatForm();
      catOnlineConfigs.value = [];
    }
    await loadCatPans();
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    interfaceLoading.value = false;
  }
}

function toggleCatServerDropdown() {
  if (!catServerOptions.value.length) return;
  catServerDropdownOpen.value = !catServerDropdownOpen.value;
}

function toggleCatSyncFromServerDropdown() {
  if (!catSyncFromServerOptions.value.length || catRemoteLoading.value || catSaving.value) return;
  catSyncFromServerDropdownOpen.value = !catSyncFromServerDropdownOpen.value;
}

function selectCatSyncFromServer(key) {
  catSyncFromServerKey.value = typeof key === 'string' ? key.trim() : '';
  closeCatSyncFromServerDropdown();
}

async function selectCatServer(key) {
  const nextKey = typeof key === 'string' ? key.trim() : '';
  if (!nextKey) return;
  if (nextKey === '__new__') {
    enterCatServerAddMode();
    closeCatServerDropdown();
    return;
  }
  catSelectedServerKey.value = nextKey;
  catServerAddMode.value = false;
  catSyncFromServerKey.value = '';
  closeCatServerDropdown();
  closeCatSyncFromServerDropdown();
  await hydrateSelectedCatServer();
  await loadCatPans();
}

function enterCatServerAddMode() {
  catServerPrevSelectedKey.value = catSelectedServerKey.value || ((catServers.value[0] && catServers.value[0].name) || '');
  catServerAddMode.value = true;
  catSelectedServerKey.value = '__new__';
  catSyncFromServerKey.value = '';
  closeCatServerDropdown();
  closeCatSyncFromServerDropdown();
  resetCatForm();
}

async function cancelCatServerAddMode() {
  catServerAddMode.value = false;
  catSelectedServerKey.value = catServerPrevSelectedKey.value || ((catServers.value[0] && catServers.value[0].name) || '');
  catSyncFromServerKey.value = '';
  closeCatServerDropdown();
  closeCatSyncFromServerDropdown();
  if (catSelectedServerKey.value) {
    await hydrateSelectedCatServer();
    await loadCatPans();
  } else {
    resetCatForm();
    catPans.value = [];
  }
}

async function toggleCatServerAddMode() {
  if (catServerAddMode.value) {
    await cancelCatServerAddMode();
    return;
  }
  enterCatServerAddMode();
}

function upsertCatServerRow(list, previousKey, nextRow) {
  const rows = Array.isArray(list) ? list.map(normalizeCatServerRow).filter((item) => item.name) : [];
  const target = normalizeCatServerRow(nextRow);
  if (!target.name) return rows;
  const prev = typeof previousKey === 'string' ? previousKey.trim() : '';
  const prevIndex = prev ? rows.findIndex((item) => item && item.name === prev) : -1;
  if (prevIndex >= 0) {
    rows[prevIndex] = target;
    return rows;
  }
  const sameNameIndex = rows.findIndex((item) => item && item.name === target.name);
  if (sameNameIndex >= 0) {
    rows[sameNameIndex] = target;
    return rows;
  }
  rows.push(target);
  return rows;
}

async function saveCatServer() {
  if (catSaving.value || !canSaveCatServer.value) return;
  catSaving.value = true;
  let saved = false;
  try {
    const isAddingServer = catServerAddMode.value;
    const previousServerKey = catSelectedServerKey.value;
    const previousNormalizedApiBase = savedSelectedCatApiBase.value;
    const nextNormalizedApiBase = normalizedCatApiBase.value;
    const baseChanged = isAddingServer || nextNormalizedApiBase !== previousNormalizedApiBase;
    const syncFromKey = String(catSyncFromServerKey.value || '');
    const payload = {
      catpawrunnerServerKey: isAddingServer ? '__new__' : previousServerKey,
      catpawrunnerName: catForm.value.name.trim(),
      catpawrunnerApiBase: nextNormalizedApiBase
    };
    await saveDashboardCatpawrunnerServer(payload);
    saved = true;
    catServers.value = upsertCatServerRow(catServers.value, previousServerKey, {
      name: payload.catpawrunnerName,
      apiBase: payload.catpawrunnerApiBase
    });
    catSelectedServerKey.value = payload.catpawrunnerName;
    catServerAddMode.value = false;
    if (baseChanged) {
      if (syncFromKey) {
        const sourceApiBase = syncFromKey === '__current__'
          ? previousNormalizedApiBase
          : ((catServers.value.find((server) => server.name === syncFromKey) || {}).apiBase || '');
        if (!normalizeHttpBase(sourceApiBase)) {
          throw new Error('同步来源服务器无效（已保存）');
        }
        try {
          await syncCatSettingsBetweenServers({
            sourceApiBase,
            targetApiBase: nextNormalizedApiBase
          });
        } catch (err) {
          throw new Error(`${(err && err.message) || '同步失败'}（已保存）`);
        }
      } else {
        try {
          await fetchCatpawrunnerAdminSettings(nextNormalizedApiBase);
        } catch (_err) {
          throw new Error('CatPawRunner 接口异常（已保存）');
        }
      }
    } else {
      await persistSelectedCatRemoteSettings(nextNormalizedApiBase);
    }
    await refreshCatServerAfterSave({ silent: true });
    notifySuccess('保存成功');
  } catch (err) {
    if (saved) {
      try {
        await refreshCatServerAfterSave({ silent: true });
      } catch (_e) {}
    }
    notifyError((err && err.message) || '保存失败');
  } finally {
    catSaving.value = false;
  }
}

async function saveCatPans() {
  if (catPansSaving.value) return;
  catPansSaving.value = true;
  try {
    const payload = normalizeCatPansForSave();
    const runtimeId = getRuntimeIdFromOnlineConfigs();
    let next = payload;
    if (normalizedCatApiBase.value && runtimeId) {
      try {
        const putResp = await saveCatpawrunnerWebsitePans(normalizedCatApiBase.value, runtimeId, payload);
        const putData = unwrapCatpawrunnerWebsiteData(putResp);
        const updated = Array.isArray(putData) ? normalizeCatPansForRuntime(putData) : null;
        next = updated || payload;
      } catch (_e) {
        throw new Error('CatPawRunner 接口异常');
      }
    }
    const data = await saveDashboardVideoPans(next);
    applyCatPans(Array.isArray(data.pans) ? data.pans : next);
    notifySuccess('网盘列表已保存');
  } catch (err) {
    notifyError((err && err.message) || '网盘列表保存失败');
  } finally {
    catPansSaving.value = false;
  }
}

function normalizeVideoSourceRow(site) {
  const row = site && typeof site === 'object' ? site : {};
  const rawError =
    typeof row.error === 'string'
      ? row.error
      : typeof row.errorMessage === 'string'
        ? row.errorMessage
        : typeof row.err === 'string'
          ? row.err
          : '';
  return {
    key: typeof row.key === 'string' ? row.key : '',
    name: typeof row.name === 'string' ? row.name : '',
    api: typeof row.api === 'string' ? row.api : '',
    type: typeof row.type === 'number' ? row.type : undefined,
    availability: typeof row.availability === 'string' ? row.availability : 'unchecked',
    enabled: row.enabled !== false,
    home: row.home !== false,
    search: row.search !== false,
    error: rawError || ''
  };
}

function formatVideoSourceApi(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function measureCellWidth(list, getText, minPx, maxPx, className = '') {
  if (typeof document === 'undefined') return minPx;
  const measure = document.createElement('span');
  measure.className = className;
  measure.style.position = 'absolute';
  measure.style.visibility = 'hidden';
  measure.style.whiteSpace = 'nowrap';
  measure.style.left = '-99999px';
  measure.style.top = '-99999px';
  document.body.appendChild(measure);
  let maxNamePx = 0;
  (Array.isArray(list) ? list : []).forEach((item) => {
    measure.textContent = getText(item);
    maxNamePx = Math.max(maxNamePx, Math.ceil(measure.getBoundingClientRect().width));
  });
  document.body.removeChild(measure);
  return Math.min(Math.max(maxNamePx, minPx), maxPx);
}

function applyVideoSourceSites(sites, coverSite = '') {
  const normalized = (Array.isArray(sites) ? sites : []).map(normalizeVideoSourceRow).filter((item) => item.key);
  videoSourceNameWidthPx.value = measureCellWidth(
    normalized,
    (site) => (site && (site.name || site.key)) || '',
    80,
    420,
    'adm-text-sm adm-font-medium'
  );
  videoSourceApiWidthPx.value = measureCellWidth(
    normalized,
    (site) => formatVideoSourceApi(site && site.api),
    90,
    360,
    'adm-text-xs'
  );
  videoSourceSites.value = normalized;
  videoSourceCoverSite.value = typeof coverSite === 'string' ? coverSite.trim() : '';
  const validKeys = new Set(normalized.map((site) => site.key));
  selectedVideoSourceKeys.value = selectedVideoSourceKeys.value.filter((key) => validKeys.has(key));
}

async function persistVideoSourceToggle(site, field, checked, updater) {
  const key = String(site && site.key || '').trim();
  if (!key) return;
  const prev = !!site[field];
  site[field] = !!checked;
  try {
    const data = await updater(key, !!checked);
    site[field] = data && typeof data[field] === 'boolean' ? data[field] : !!checked;
  } catch (err) {
    site[field] = prev;
    notifyError((err && err.message) || '保存失败');
  }
}

async function bulkUpdateVideoSourceFlags(keys, updater, enabled) {
  const list = Array.isArray(keys) ? keys.filter(Boolean) : [];
  for (const key of list) {
    const site = videoSourceSites.value.find((item) => item.key === key);
    if (!site) continue;
    try {
      const data = await updater(key, enabled);
      if (typeof data?.enabled === 'boolean') site.enabled = data.enabled;
      if (typeof data?.home === 'boolean') site.home = data.home;
      if (typeof data?.search === 'boolean') site.search = data.search;
    } catch (_e) {}
  }
}

function availabilityTagClass(status) {
  const normalized = typeof status === 'string' ? status.trim().toLowerCase() : '';
  if (normalized === 'valid' || normalized === 'online') return 'tag-green';
  if (normalized === 'invalid' || normalized === 'offline' || normalized === 'error') return 'tag-red';
  if (normalized === 'skipped') return 'tag-gray';
  return 'tag-gray';
}

function availabilityText(status) {
  const normalized = typeof status === 'string' ? status.trim().toLowerCase() : '';
  if (normalized === 'valid' || normalized === 'online') return '通过';
  if (normalized === 'invalid' || normalized === 'offline' || normalized === 'error') return '失效';
  if (normalized === 'skipped') return '跳过';
  return '未检测';
}

function toggleAllVideoSources(checked) {
  if (!checked) {
    selectedVideoSourceKeys.value = [];
    return;
  }
  selectedVideoSourceKeys.value = videoSourceSites.value.map((site) => site.key).filter(Boolean);
}

function toggleVideoSourceSelected(key, checked) {
  const target = String(key || '').trim();
  if (!target) return;
  const current = new Set(selectedVideoSourceKeys.value);
  if (checked) current.add(target);
  else current.delete(target);
  selectedVideoSourceKeys.value = Array.from(current);
}

function moveVideoSource(index, direction) {
  const current = Array.isArray(videoSourceSites.value) ? [...videoSourceSites.value] : [];
  if (!current.length) return;
  const from = Number.isFinite(Number(index)) ? Math.trunc(Number(index)) : -1;
  if (from < 0 || from >= current.length) return;
  const to = direction === 'up' ? from - 1 : direction === 'down' ? from + 1 : from;
  if (to < 0 || to >= current.length || to === from) return;
  const [item] = current.splice(from, 1);
  current.splice(to, 0, item);
  videoSourceSites.value = current;
}

async function resolveVideoCatApiBase() {
  const apiBase = await resolveDashboardCatpawrunnerApiBase(normalizedCatApiBase.value);
  if (!apiBase) return '';
  if (!normalizedCatApiBase.value && catSelectedServerKey.value) {
    const current = catServers.value.find((server) => server.name === catSelectedServerKey.value);
    if (current && !normalizeHttpBase(current.apiBase)) {
      current.apiBase = apiBase;
    }
  }
  return apiBase;
}

async function loadVideoPanel() {
  if (!isAdmin.value || videoLoading.value) return;
  videoLoading.value = true;
  try {
    const data = await fetchDashboardVideoSourceSites();
    applyVideoSourceSites(data && data.sites, data && data.coverSite);
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    videoLoading.value = false;
  }
}

function applyMagicSettings(data) {
  const root = data && typeof data === 'object' ? data : {};
  magicEpisodeCleanRegexRules.value = Array.isArray(root.episodeCleanRegexRules)
    ? root.episodeCleanRegexRules.map((it) => String(it || '').trim()).filter(Boolean)
    : (typeof root.episodeCleanRegex === 'string' && root.episodeCleanRegex.trim() ? [root.episodeCleanRegex.trim()] : []);
  magicEpisodeRules.value = Array.isArray(root.episodeRules)
    ? root.episodeRules.map(decodeEpisodeRule).filter(Boolean)
    : [];
  magicMovieRules.value = Array.isArray(root.movieRules)
    ? root.movieRules.map(decodeEpisodeRule).filter(Boolean)
    : [];
  magicAggregateRegexRules.value = Array.isArray(root.aggregateRegexRules)
    ? root.aggregateRegexRules.map((it) => String(it || '').trim()).filter(Boolean)
    : [];
  magicShadowSmartSettings.value = {
    smartSourcePriorityTokens: Array.isArray(root.smartSourcePriorityTokens) ? root.smartSourcePriorityTokens.slice() : [],
    smartPanMatchTokens: Array.isArray(root.smartPanMatchTokens) ? root.smartPanMatchTokens.slice() : [],
    smartPanAliasMappings: Array.isArray(root.smartPanAliasMappings) ? root.smartPanAliasMappings.map((it) => ({ ...it })) : []
  };
}

async function loadMagicPanel() {
  if (!isAdmin.value || magicLoading.value) return;
  magicLoading.value = true;
  try {
    const data = await fetchMagicSettings();
    applyMagicSettings(data);
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    magicLoading.value = false;
  }
}

function buildMagicSavePayload() {
  return {
    episodeCleanRegexRules: magicEpisodeCleanRegexRules.value.map((it) => String(it || '').trim()).filter(Boolean),
    episodeRules: magicEpisodeRules.value.map(normalizeMagicRuleRow).map(encodeEpisodeRule).filter(Boolean),
    movieRules: magicMovieRules.value.map(normalizeMagicRuleRow).map(encodeEpisodeRule).filter(Boolean),
    aggregateRegexRules: magicAggregateRegexRules.value.map((it) => String(it || '').trim()).filter(Boolean),
    smartSourcePriorityTokens: magicShadowSmartSettings.value.smartSourcePriorityTokens,
    smartPanMatchTokens: magicShadowSmartSettings.value.smartPanMatchTokens,
    smartPanAliasMappings: magicShadowSmartSettings.value.smartPanAliasMappings
  };
}

async function saveMagicBlock(successMessage = '保存成功') {
  if (magicSaving.value) return;
  magicSaving.value = true;
  try {
    const data = await saveMagicSettings(buildMagicSavePayload());
    applyMagicSettings(data);
    notifySuccess(successMessage);
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    magicSaving.value = false;
  }
}

async function loadSmartMatchBlockKeywords() {
  if (!isAdmin.value || smartMatchBlockKeywordsLoading.value) return;
  smartMatchBlockKeywordsLoading.value = true;
  try {
    const data = await fetchSmartMatchBlockKeywords();
    const keywords = data && Array.isArray(data.keywords) ? data.keywords : [];
    smartMatchBlockKeywords.value = keywords.map((row) => ({
      keyword: typeof row?.keyword === 'string' ? row.keyword : '',
      count: Number.isFinite(Number(row?.count)) ? Number(row.count) : 0
    })).filter((row) => row.keyword);
    const stillExists = smartMatchBlockSelectedKeyword.value
      && smartMatchBlockKeywords.value.some((row) => row.keyword === smartMatchBlockSelectedKeyword.value);
    if (!stillExists) {
      smartMatchBlockSelectedKeyword.value = '';
      smartMatchBlockItems.value = [];
    }
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    smartMatchBlockKeywordsLoading.value = false;
  }
}

async function loadSmartMatchBlockItems(keyword) {
  const targetKeyword = typeof keyword === 'string' ? keyword.trim() : '';
  if (!targetKeyword) {
    smartMatchBlockSelectedKeyword.value = '';
    smartMatchBlockItems.value = [];
    return;
  }
  if (smartMatchBlockItemsLoading.value) return;
  smartMatchBlockItemsLoading.value = true;
  smartMatchBlockSelectedKeyword.value = targetKeyword;
  try {
    const data = await fetchSmartMatchBlockItems(targetKeyword);
    smartMatchBlockItems.value = Array.isArray(data && data.items) ? data.items.map((item) => ({
      keyword: typeof item?.keyword === 'string' ? item.keyword : targetKeyword,
      siteName: typeof item?.siteName === 'string' ? item.siteName : '',
      siteKey: typeof item?.siteKey === 'string' ? item.siteKey : '',
      spiderApi: typeof item?.spiderApi === 'string' ? item.spiderApi : '',
      siteDetail: typeof item?.siteDetail === 'string' ? item.siteDetail : '',
      poster: typeof item?.poster === 'string' ? item.poster : '',
      panFlag: typeof item?.panFlag === 'string' ? item.panFlag : '',
      source: typeof item?.source === 'string' ? item.source : ''
    })) : [];
  } catch (err) {
    smartMatchBlockItems.value = [];
    notifyError((err && err.message) || '加载失败');
  } finally {
    smartMatchBlockItemsLoading.value = false;
  }
}

async function loadSmartPanel(force = false) {
  if (!isAdmin.value || smartLoading.value) return;
  if (smartLoaded.value && !force) return;
  smartLoading.value = true;
  try {
    const data = await fetchSmartSettings();
    applySmartSettings(data);
    await loadSmartMatchBlockKeywords();
    await loadManualTmdbItems();
    smartLoaded.value = true;
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    smartLoading.value = false;
  }
}

async function saveSmartPanel(successMessage = '保存成功') {
  if (smartSaving.value) return;
  smartSaving.value = true;
  try {
    const data = await saveSmartSettings(buildSmartSavePayload());
    applySmartSettings(data);
    notifySuccess(successMessage);
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    smartSaving.value = false;
  }
}

function restoreSmartPanDefaults() {
  smartPanDefaultsConfirming.value = false;
  smartSourceRuleRows.value = buildDefaultSmartSourceRuleRows();
  smartSourcePriorityTokensInput.value = DEFAULT_SMART_SOURCE_PRIORITY_TOKENS.join(',');
  smartPanMatchTokensInput.value = DEFAULT_SMART_PAN_MATCH_TOKENS.join(',');
  smartPanAliasMappings.value = DEFAULT_SMART_PAN_ALIAS_MAPPINGS.map((item) => ({ ...item }));
  void saveSmartPanel('恢复默认并保存成功');
}

function restoreSmartSiteCleanDefaults() {
  smartSiteCleanDefaultsConfirming.value = false;
  smartSiteCleanKeywordsInput.value = DEFAULT_SMART_SITE_CLEAN_KEYWORDS.join(',');
  void saveSmartPanel('恢复默认并保存成功');
}

function addSmartPanAliasMapping() {
  if (smartSaving.value) return;
  const pan = stripDisplaySequencePrefix(smartPanAliasMapPanInput.value || '');
  const aliases = normalizeCommaTokenLine(smartPanAliasMapAliasesInput.value || '').join(',');
  if (!pan) {
    notifyError('请先填写网盘');
    return;
  }
  const next = smartPanAliasMappings.value.slice();
  const index = next.findIndex((item) => String(item?.pan || '').trim().toLowerCase() === pan.toLowerCase());
  const row = { pan, aliases };
  if (index >= 0) next[index] = row;
  else next.push(row);
  smartPanAliasMappings.value = normalizeSmartPanAliasMappings(next);
  smartPanAliasMapPanInput.value = '';
  smartPanAliasMapAliasesInput.value = '';
}

function removeSmartPanAliasMapping(index) {
  smartPanAliasMappings.value = smartPanAliasMappings.value.filter((_, currentIndex) => currentIndex !== index);
}

async function toggleSmartMatchBlockKeyword(keyword) {
  const targetKeyword = typeof keyword === 'string' ? keyword.trim() : '';
  if (!targetKeyword) return;
  if (targetKeyword === smartMatchBlockSelectedKeyword.value) {
    smartMatchBlockSelectedKeyword.value = '';
    smartMatchBlockItems.value = [];
    return;
  }
  await loadSmartMatchBlockItems(targetKeyword);
}

async function removeSmartMatchBlockItem(item) {
  if (smartMatchBlockItemsLoading.value) return;
  const keyword = typeof item?.keyword === 'string' ? item.keyword.trim() : '';
  const siteKey = typeof item?.siteKey === 'string' ? item.siteKey.trim() : '';
  const siteDetail = typeof item?.siteDetail === 'string' ? item.siteDetail.trim() : '';
  const panFlag = typeof item?.panFlag === 'string' ? item.panFlag.trim() : '';
  const source = typeof item?.source === 'string' ? item.source.trim() : '';
  if (!keyword || !siteKey || !siteDetail) return;
  try {
    await deleteSmartMatchBlockItem({ keyword, siteKey, siteDetail, panFlag, source });
    await loadSmartMatchBlockItems(keyword);
    await loadSmartMatchBlockKeywords();
    notifySuccess('已删除');
  } catch (err) {
    notifyError((err && err.message) || '删除失败');
  }
}

async function removeSmartMatchBlockKeyword(keyword) {
  const targetKeyword = typeof keyword === 'string' ? keyword.trim() : '';
  if (!targetKeyword || smartMatchBlockKeywordsLoading.value || smartMatchBlockItemsLoading.value) return;
  try {
    await deleteSmartMatchBlockKeyword({ keyword: targetKeyword });
    if (smartMatchBlockSelectedKeyword.value === targetKeyword) {
      smartMatchBlockSelectedKeyword.value = '';
      smartMatchBlockItems.value = [];
    }
    await loadSmartMatchBlockKeywords();
    notifySuccess('已删除');
  } catch (err) {
    notifyError((err && err.message) || '删除失败');
  }
}

function handleSmartMatchBlockUpdated() {
  if (!smartLoaded.value) return;
  void loadSmartMatchBlockKeywords();
  if (smartMatchBlockSelectedKeyword.value) {
    void loadSmartMatchBlockItems(smartMatchBlockSelectedKeyword.value);
  }
}

function normalizeManualTMDBType(value) {
  const type = String(value || '').trim().toLowerCase();
  if (type === 'tv' || type === 'movie') return type;
  return '';
}

function buildManualTmdbKey(tmdbType, tmdbId) {
  const type = normalizeManualTMDBType(tmdbType);
  const id = Number.isFinite(Number(tmdbId)) ? Math.trunc(Number(tmdbId)) : 0;
  if (!type || id <= 0) return '';
  return `${type}:${id}`;
}

function clearManualTmdbSelection() {
  manualTmdbSelectedTmdbType.value = '';
  manualTmdbSelectedTmdbId.value = 0;
}

function isManualTmdbItemSelected(item) {
  return buildManualTmdbKey(item?.tmdbType, item?.tmdbId) === manualTmdbSelectedKey.value;
}

async function loadManualTmdbItems() {
  if (!isAdmin.value || manualTmdbItemsLoading.value) return;
  manualTmdbItemsLoading.value = true;
  try {
    const data = await fetchDashboardSmartManualTMDBItems();
    const rows = Array.isArray(data?.items) ? data.items : [];
    manualTmdbItems.value = rows.map((item) => ({
      tmdbType: normalizeManualTMDBType(item?.tmdbType),
      tmdbId: Number.isFinite(Number(item?.tmdbId)) ? Math.trunc(Number(item.tmdbId)) : 0,
      title: typeof item?.title === 'string' ? item.title.trim() : '',
      count: Number.isFinite(Number(item?.count)) ? Math.max(0, Math.trunc(Number(item.count))) : 0
    })).filter((item) => item.tmdbType && item.tmdbId > 0 && item.title);
    if (!manualTmdbItems.value.some((item) => buildManualTmdbKey(item.tmdbType, item.tmdbId) === manualTmdbSelectedKey.value)) {
      clearManualTmdbSelection();
    }
  } catch (err) {
    manualTmdbItems.value = [];
    clearManualTmdbSelection();
    notifyError((err && err.message) || '加载失败');
  } finally {
    manualTmdbItemsLoading.value = false;
  }
}

function buildManualItemDraft() {
  return {
    siteKey: '',
    siteDetail: '',
    panFlag: '',
    seasonHint: '',
    autoDisable: true
  };
}

function buildManualItemEditDraft(row = null) {
  return {
    siteKey: typeof row?.siteKey === 'string' ? row.siteKey : '',
    spiderApi: typeof row?.spiderApi === 'string' ? row.spiderApi : '',
    siteDetail: typeof row?.siteDetail === 'string' ? row.siteDetail : '',
    panFlag: typeof row?.panFlag === 'string' ? row.panFlag : '',
    seasonHint: typeof row?.seasonHint === 'string' ? row.seasonHint : '',
    autoDisable: row?.autoDisable !== false
  };
}

async function loadManualItemRows() {
  if (!isAdmin.value || manualItemRowsLoading.value) return;
  const tmdbType = normalizeManualTMDBType(manualTmdbSelectedTmdbType.value);
  const tmdbId = manualTmdbSelectedTmdbId.value;
  if (!tmdbType || !tmdbId) {
    manualItemRows.value = [];
    return;
  }
  manualItemRowsLoading.value = true;
  try {
    const data = await fetchDashboardSmartManualItems(tmdbType, tmdbId);
    const rows = Array.isArray(data?.items) ? data.items : [];
    manualItemRows.value = rows.map((item) => ({
      id: Number.isFinite(Number(item?.id)) ? Math.trunc(Number(item.id)) : 0,
      tmdbType: normalizeManualTMDBType(item?.tmdbType),
      tmdbId: Number.isFinite(Number(item?.tmdbId)) ? Math.trunc(Number(item.tmdbId)) : 0,
      siteKey: typeof item?.siteKey === 'string' ? item.siteKey.trim() : '',
      spiderApi: typeof item?.spiderApi === 'string' ? item.spiderApi.trim() : '',
      siteDetail: typeof item?.siteDetail === 'string' ? item.siteDetail.trim() : '',
      panFlag: typeof item?.panFlag === 'string' ? item.panFlag.trim() : '',
      seasonHint: typeof item?.seasonHint === 'string' ? item.seasonHint.trim() : '',
      autoDisable: item?.autoDisable !== false,
      enabled: item?.enabled !== false
    })).filter((item) => item.id > 0 && item.tmdbType && item.tmdbId > 0);
    manualTmdbItems.value = manualTmdbItems.value.map((item) => (
      buildManualTmdbKey(item.tmdbType, item.tmdbId) === manualTmdbSelectedKey.value
        ? { ...item, count: manualItemRows.value.length }
        : item
    ));
  } catch (err) {
    manualItemRows.value = [];
    notifyError((err && err.message) || '加载失败');
  } finally {
    manualItemRowsLoading.value = false;
  }
}

function toggleManualTmdbItem(item) {
  const tmdbType = normalizeManualTMDBType(item?.tmdbType);
  const tmdbId = Number.isFinite(Number(item?.tmdbId)) ? Math.trunc(Number(item.tmdbId)) : 0;
  const key = buildManualTmdbKey(tmdbType, tmdbId);
  if (!key) return;
  if (manualTmdbSelectedKey.value === key) {
    clearManualTmdbSelection();
    return;
  }
  manualTmdbSelectedTmdbType.value = tmdbType;
  manualTmdbSelectedTmdbId.value = tmdbId;
}

function isManualRowSourceMode(row) {
  return !!String(row?.siteKey || '').trim()
    || !!String(row?.spiderApi || '').trim()
    || !!String(row?.siteDetail || '').trim();
}

function isManualRowPanMode(row) {
  return !!String(row?.panFlag || '').trim();
}

function openManualItemRowEditor(row) {
  const id = Number.isFinite(Number(row?.id)) ? Math.trunc(Number(row.id)) : 0;
  if (id <= 0 || manualItemUpdating.value) return;
  manualItemEditorOpen.value = false;
  manualItemDraft.value = buildManualItemDraft();
  manualItemEditingId.value = id;
  manualItemEditDraft.value = buildManualItemEditDraft(row);
}

function cancelManualItemRowEditor() {
  manualItemEditingId.value = 0;
  manualItemEditDraft.value = buildManualItemEditDraft();
}

function toggleManualItemEditor() {
  if (!manualTmdbSelectedReady.value) return;
  if (manualItemEditorOpen.value) {
    manualItemEditorOpen.value = false;
    manualItemDraft.value = buildManualItemDraft();
    return;
  }
  cancelManualItemRowEditor();
  manualItemEditorOpen.value = true;
  manualItemDraft.value = buildManualItemDraft();
}

function cancelManualItemEditor() {
  manualItemEditorOpen.value = false;
  manualItemDraft.value = buildManualItemDraft();
}

async function saveManualItemRowEditor(row) {
  const id = Number.isFinite(Number(row?.id)) ? Math.trunc(Number(row.id)) : 0;
  if (id <= 0 || manualItemEditingId.value !== id || manualItemUpdating.value) return;
  const sourceMode = isManualRowSourceMode(row);
  const panMode = isManualRowPanMode(row);
  const nextSiteKey = sourceMode
    ? String(manualItemEditDraft.value?.siteKey || '').trim()
    : String(row?.siteKey || '').trim();
  const nextSpiderApi = sourceMode
    ? String(manualItemEditDraft.value?.spiderApi || '').trim()
    : String(row?.spiderApi || '').trim();
  const nextSiteDetail = sourceMode
    ? String(manualItemEditDraft.value?.siteDetail || '').trim()
    : String(row?.siteDetail || '').trim();
  const nextPanFlag = panMode
    ? String(manualItemEditDraft.value?.panFlag || '').trim()
    : String(row?.panFlag || '').trim();
  if (panMode && !nextPanFlag) {
    notifyError('请先填写 panflag');
    return;
  }
  manualItemUpdating.value = true;
  try {
    await updateDashboardSmartManualItem({
      id,
      siteKey: nextSiteKey,
      spiderApi: nextSpiderApi,
      siteDetail: nextSiteDetail,
      panFlag: nextPanFlag,
      seasonHint: String(manualItemEditDraft.value?.seasonHint || '').trim(),
      autoDisable: !!manualItemEditDraft.value?.autoDisable
    });
    await loadManualItemRows();
    cancelManualItemRowEditor();
    notifySuccess('保存成功');
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    manualItemUpdating.value = false;
  }
}

async function saveManualItemDraft() {
  if (!manualTmdbSelectedReady.value || manualItemSaving.value) return;
  const panFlag = String(manualItemDraft.value?.panFlag || '').trim();
  if (!panFlag) {
    notifyError('请先填写 panflag');
    return;
  }
  manualItemSaving.value = true;
  try {
    await addDashboardSmartManualItem({
      tmdbType: manualTmdbSelectedTmdbType.value,
      tmdbId: manualTmdbSelectedTmdbId.value,
      siteKey: String(manualItemDraft.value?.siteKey || '').trim(),
      siteDetail: String(manualItemDraft.value?.siteDetail || '').trim(),
      panFlag,
      seasonHint: String(manualItemDraft.value?.seasonHint || '').trim(),
      autoDisable: !!manualItemDraft.value?.autoDisable
    });
    await loadManualItemRows();
    cancelManualItemEditor();
    notifySuccess('保存成功');
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    manualItemSaving.value = false;
  }
}

async function removeManualItemRow(row) {
  const id = Number.isFinite(Number(row?.id)) ? Math.trunc(Number(row.id)) : 0;
  if (id <= 0 || manualItemDeletingId.value === id) return;
  if (manualItemEditingId.value === id) {
    cancelManualItemRowEditor();
  }
  manualItemDeletingId.value = id;
  try {
    await deleteDashboardSmartManualItem({ id });
    await loadManualItemRows();
    notifySuccess('已删除');
  } catch (err) {
    notifyError((err && err.message) || '删除失败');
  } finally {
    manualItemDeletingId.value = 0;
  }
}

async function removeManualTmdbItem(item) {
  const tmdbType = normalizeManualTMDBType(item?.tmdbType);
  const tmdbId = Number.isFinite(Number(item?.tmdbId)) ? Math.trunc(Number(item.tmdbId)) : 0;
  const key = buildManualTmdbKey(tmdbType, tmdbId);
  if (!key || manualTmdbItemsLoading.value) return;
  if (manualTmdbDeletingKey.value === key) return;
  manualTmdbDeletingKey.value = key;
  try {
    await deleteDashboardSmartManualTMDBItem({ tmdbType, tmdbId });
    if (manualTmdbSelectedKey.value === key) {
      clearManualTmdbSelection();
      manualItemEditorOpen.value = false;
      manualItemDraft.value = buildManualItemDraft();
      manualItemRows.value = [];
    }
    await loadManualTmdbItems();
    notifySuccess('已删除');
  } catch (err) {
    notifyError((err && err.message) || '删除失败');
  } finally {
    manualTmdbDeletingKey.value = '';
  }
}

function openManualTmdbDialog() {
  manualTmdbDialogKeyword.value = '';
  manualTmdbDialogOpen.value = true;
}

function closeManualTmdbDialog() {
  manualTmdbDialogOpen.value = false;
}

function confirmManualTmdbSelectionByDialog(selected) {
  const current = selected && typeof selected === 'object' ? selected : null;
  if (!current || manualTmdbAdding.value) return;
  const tmdbType = normalizeManualTMDBType(current.tmdbType);
  const tmdbId = Number(current.tmdbId);
  const title = typeof current.title === 'string' ? current.title.trim() : '';
  if (!tmdbType || !Number.isFinite(tmdbId) || tmdbId <= 0 || !title) return;
  manualTmdbAdding.value = true;
  addDashboardSmartManualTMDBItem({
    tmdbType,
    tmdbId,
    title
  }).then(async () => {
    await loadManualTmdbItems();
    manualTmdbSelectedTmdbType.value = tmdbType;
    manualTmdbSelectedTmdbId.value = tmdbId;
    closeManualTmdbDialog();
    notifySuccess('添加成功');
  }).catch((err) => {
    notifyError((err && err.message) || '添加失败');
  }).finally(() => {
    manualTmdbAdding.value = false;
  });
}

function addMagicEpisodeCleanRule() {
  const value = normalizeAggregateRegexRuleInput(magicEpisodeCleanRuleInput.value || '');
  if (!value) return;
  magicEpisodeCleanRegexRules.value = [...magicEpisodeCleanRegexRules.value, value];
  magicEpisodeCleanRuleInput.value = '';
  magicEpisodeDefaultsConfirming.value = false;
  void saveMagicBlock();
}

function removeMagicEpisodeCleanRule(index) {
  magicEpisodeCleanRegexRules.value = magicEpisodeCleanRegexRules.value.filter((_, currentIndex) => currentIndex !== index);
  void saveMagicBlock();
}

function addMagicEpisodeRule() {
  const parsed = normalizePatternInput(magicEpisodeRulePatternInput.value || '');
  if (!parsed || !parsed.pattern) return;
  magicEpisodeRules.value = [
    ...magicEpisodeRules.value,
    { pattern: parsed.pattern, replace: String(magicEpisodeRuleReplaceInput.value || '').trim(), flags: parsed.flags || '' }
  ];
  magicEpisodeRulePatternInput.value = '';
  magicEpisodeRuleReplaceInput.value = '';
  magicEpisodeDefaultsConfirming.value = false;
  void saveMagicBlock();
}

function removeMagicEpisodeRule(index) {
  magicEpisodeRules.value = magicEpisodeRules.value.filter((_, currentIndex) => currentIndex !== index);
  void saveMagicBlock();
}

function restoreMagicEpisodeDefaults() {
  magicEpisodeCleanRegexRules.value = DEFAULT_EPISODE_CLEAN_RULES.slice();
  magicEpisodeRules.value = DEFAULT_EPISODE_RULES.map((rule) => ({ ...rule }));
  magicEpisodeCleanRuleInput.value = '';
  magicEpisodeRulePatternInput.value = '';
  magicEpisodeRuleReplaceInput.value = '';
  magicEpisodeDefaultsConfirming.value = false;
  void saveMagicBlock('恢复默认并保存成功');
}

function addMagicMovieRule() {
  const parsed = normalizePatternInput(magicMovieRulePatternInput.value || '');
  if (!parsed || !parsed.pattern) return;
  magicMovieRules.value = [
    ...magicMovieRules.value,
    { pattern: parsed.pattern, replace: String(magicMovieRuleReplaceInput.value || '').trim(), flags: parsed.flags || '' }
  ];
  magicMovieRulePatternInput.value = '';
  magicMovieRuleReplaceInput.value = '';
  magicMovieDefaultsConfirming.value = false;
  void saveMagicBlock();
}

function removeMagicMovieRule(index) {
  magicMovieRules.value = magicMovieRules.value.filter((_, currentIndex) => currentIndex !== index);
  void saveMagicBlock();
}

function restoreMagicMovieDefaults() {
  magicMovieRules.value = DEFAULT_MOVIE_RULES.map((rule) => ({ ...rule }));
  magicMovieRulePatternInput.value = '';
  magicMovieRuleReplaceInput.value = '';
  magicMovieDefaultsConfirming.value = false;
  void saveMagicBlock('恢复默认并保存成功');
}

function addMagicAggregateRule() {
  const value = normalizeAggregateRegexRuleInput(magicAggregateRuleInput.value || '');
  if (!value) return;
  magicAggregateRegexRules.value = [...magicAggregateRegexRules.value, value];
  magicAggregateRuleInput.value = '';
  magicAggregateDefaultsConfirming.value = false;
  void saveMagicBlock();
}

function removeMagicAggregateRule(index) {
  magicAggregateRegexRules.value = magicAggregateRegexRules.value.filter((_, currentIndex) => currentIndex !== index);
  void saveMagicBlock();
}

function restoreMagicAggregateDefaults() {
  magicAggregateRegexRules.value = DEFAULT_AGGREGATE_REGEX_RULES.slice();
  magicAggregateRuleInput.value = '';
  magicAggregateDefaultsConfirming.value = false;
  void saveMagicBlock('恢复默认并保存成功');
}

function runMagicEpisodeRuleTest() {
  const filename = String(magicEpisodeRuleTestInput.value || '').trim();
  if (!filename) {
    setMagicTestOutput(magicEpisodeRuleTestOutput, '', '请输入文件名');
    return;
  }
  const list = Array.isArray(magicEpisodeRules.value) ? magicEpisodeRules.value : [];
  if (!list.length) {
    setMagicTestOutput(magicEpisodeRuleTestOutput, 'error', '无匹配规则');
    return;
  }
  let cleaned = normalizeSeasonEpisodeMarkers(filename);
  magicEpisodeCleanRegexRules.value.forEach((rule) => {
    const regex = buildRegexFromInput(rule, { defaultFlags: 'ig', forceGlobal: true });
    if (regex) cleaned = cleaned.replace(regex, '');
  });
  cleaned = normalizeSeasonEpisodeMarkers(cleaned.replace(/\s+/g, ' ').trim());
  const failures = [];
  for (let index = 0; index < list.length; index += 1) {
    const rule = list[index];
    let regex = null;
    try {
      regex = new RegExp(String(rule.pattern || '').trim(), String(rule.flags || '').trim() || 'i');
    } catch (_error) {
      failures.push(`#${index + 1} 正则无效`);
      continue;
    }
    if (!regex.test(cleaned)) continue;
    regex.lastIndex = 0;
    const replace = normalizeReplaceTemplate(rule.replace || '');
    if (!replace) {
      setMagicTestOutput(magicEpisodeRuleTestOutput, 'success', `命中第 ${index + 1} 条：未设置 replace（无改写）`);
      return;
    }
    const output = normalizeSeasonEpisodeMarkers(cleaned.replace(regex, replace));
    if (!output) {
      setMagicTestOutput(magicEpisodeRuleTestOutput, 'error', `命中第 ${index + 1} 条，但改写失败`);
      return;
    }
    setMagicTestOutput(magicEpisodeRuleTestOutput, 'success', `命中第 ${index + 1} 条：${output}`);
    return;
  }
  if (failures.length) {
    setMagicTestOutput(magicEpisodeRuleTestOutput, 'error', `未命中（${failures.join('，')}）`);
    return;
  }
  setMagicTestOutput(magicEpisodeRuleTestOutput, 'error', '未命中');
}

function runMagicMovieRuleTest() {
  const filename = String(magicMovieRuleTestInput.value || '').trim();
  if (!filename) {
    setMagicTestOutput(magicMovieRuleTestOutput, '', '请输入文件名');
    return;
  }
  const list = Array.isArray(magicMovieRules.value) ? magicMovieRules.value : [];
  if (!list.length) {
    setMagicTestOutput(magicMovieRuleTestOutput, 'error', '无匹配规则');
    return;
  }
  const failures = [];
  for (let index = 0; index < list.length; index += 1) {
    const rule = list[index];
    let regex = null;
    try {
      regex = new RegExp(String(rule.pattern || '').trim(), String(rule.flags || '').trim() || 'i');
    } catch (_error) {
      failures.push(`#${index + 1} 正则无效`);
      continue;
    }
    if (!regex.test(filename)) continue;
    regex.lastIndex = 0;
    const replace = normalizeReplaceTemplate(rule.replace || '');
    if (!replace) {
      setMagicTestOutput(magicMovieRuleTestOutput, 'success', `命中第 ${index + 1} 条：未设置 replace（无改写）`);
      return;
    }
    const output = filename.replace(regex, replace);
    if (!output) {
      setMagicTestOutput(magicMovieRuleTestOutput, 'error', `命中第 ${index + 1} 条，但改写失败`);
      return;
    }
    setMagicTestOutput(magicMovieRuleTestOutput, 'success', `命中第 ${index + 1} 条：${output}`);
    return;
  }
  if (failures.length) {
    setMagicTestOutput(magicMovieRuleTestOutput, 'error', `未命中（${failures.join('，')}）`);
    return;
  }
  setMagicTestOutput(magicMovieRuleTestOutput, 'error', '未命中');
}

function runMagicAggregateRuleTest() {
  const query = String(magicAggregateRuleTestQueryInput.value || '').trim();
  const title = String(magicAggregateRuleTestInput.value || '').trim();
  if (!title) {
    setMagicTestOutput(magicAggregateRuleTestOutput, '', '请输入搜索结果标题');
    return;
  }
  const result = runMagicAggregateNormalization({
    query,
    title,
    rules: magicAggregateRegexRules.value,
  });
  if (result.failures.length) {
    setMagicTestOutput(magicAggregateRuleTestOutput, 'error', `清洗失败（${result.failures.join('，')}）`);
    return;
  }
  const suffix = result.groupKey ? `（聚合Key：${result.groupKey}）` : '';
  const hint = result.trailingDigits ? `（搜索尾号保护：${result.trailingDigits}）` : '';
  const noneHint = !magicAggregateRegexRules.value.length ? '（仅默认清洗）' : '';
  setMagicTestOutput(magicAggregateRuleTestOutput, 'success', `净化后：${result.cleanedTitle}${suffix}${hint}${noneHint}`);
}

async function importVideoSourcesFromCatpawrunner() {
  if (videoImporting.value) return;
  const apiBase = await resolveVideoCatApiBase();
  if (!apiBase) {
    notifyError('CatPawRunner 接口地址未设置');
    return;
  }
  videoImporting.value = true;
  try {
    const fullConfig = await fetchCatpawrunnerFullConfig(apiBase, props.bootstrap?.user?.username || '');
    const list = fullConfig && fullConfig.video && Array.isArray(fullConfig.video.sites) ? fullConfig.video.sites : [];
    const sitesPayload = list
      .map((s) => ({
        key: s && typeof s.key === 'string' ? s.key : '',
        name: s && typeof s.name === 'string' ? s.name : '',
        api: s && typeof s.api === 'string' ? s.api : '',
        type: s && typeof s.type === 'number' ? s.type : undefined
      }))
      .filter((s) => s.key && s.api);
    if (!sitesPayload.length) {
      notifyError('未获取到站源');
      return;
    }
    const imported = await importDashboardVideoSourceSites(sitesPayload);
    let nextSites = Array.isArray(imported && imported.sites) ? imported.sites : [];
    const nextCoverSite = imported && typeof imported.coverSite === 'string' ? imported.coverSite : '';
    try {
      const autoChecked = await applyDashboardVideoSourceAutoResults(nextSites);
      nextSites = Array.isArray(autoChecked && autoChecked.sites) ? autoChecked.sites : nextSites;
    } catch (_e) {}
    applyVideoSourceSites(nextSites, nextCoverSite);
    notifySuccess(`导入成功（${nextSites.length} 个站点）`);
  } catch (err) {
    notifyError((err && err.message) || '导入失败');
  } finally {
    videoImporting.value = false;
  }
}

function exportVideoSourcesToJson() {
  const sites = Array.isArray(videoSourceSites.value) ? videoSourceSites.value : [];
  const payload = {
    format: 'meowfilm.video_source.sites.v1',
    exportedAt: new Date().toISOString(),
    coverSite: videoSourceCoverSite.value || '',
    order: sites.map((site) => site.key).filter(Boolean),
    sites: sites.map((site, index) => ({
      key: site.key,
      name: site.name,
      api: site.api,
      type: site.type,
      availability: site.availability || 'unchecked',
      enabled: site.enabled !== false,
      home: site.home !== false,
      search: site.search !== false,
      cover: !!(site.key && videoSourceCoverSite.value === site.key),
      order: index + 1,
      error: site.error || ''
    }))
  };
  downloadJsonFile(payload, `meowfilm_sites_${new Date().toISOString().replace(/[:.]/g, '-')}.json`);
}

function pickVideoSourceImportFile() {
  if (videoSourceImportFileRef.value) {
    try {
      videoSourceImportFileRef.value.value = '';
    } catch (_e) {}
    videoSourceImportFileRef.value.click();
  }
}

async function importVideoSourcesFromJson(event) {
  const input = event && event.target ? event.target : null;
  const file = input && input.files && input.files[0] ? input.files[0] : null;
  if (!file) return;
  try {
    const text = await file.text();
    const parsed = JSON.parse(text);
    const sites = Array.isArray(parsed && parsed.sites) ? parsed.sites : Array.isArray(parsed) ? parsed : [];
    if (!sites.length) {
      notifyError('导入数据无站点');
      return;
    }
    const order = Array.isArray(parsed && parsed.order) ? parsed.order : [];
    const coverSite = typeof parsed && parsed && typeof parsed.coverSite === 'string' ? parsed.coverSite : '';
    const currentKeys = new Set(videoSourceSites.value.map((site) => site.key).filter(Boolean));
    const desiredStatus = {};
    const desiredHome = {};
    const desiredSearch = {};
    const results = {};
    const errors = {};
    const desiredOrder = [];
    const coverCandidates = [];
    sites.forEach((item, idx) => {
      const key = typeof item?.key === 'string' ? item.key.trim() : '';
      if (!key || !currentKeys.has(key)) return;
      desiredOrder.push({ key, order: Number.isFinite(Number(item.order)) ? Number(item.order) : idx + 1, idx });
      desiredStatus[key] = item.enabled !== false;
      desiredHome[key] = item.home !== false;
      desiredSearch[key] = item.search !== false;
      if (typeof item.availability === 'string') results[key] = item.availability;
      if (typeof item.error === 'string' && item.error.trim()) errors[key] = item.error.trim();
      if (item.cover || item.coverShown) coverCandidates.push(key);
    });
    const keys = Object.keys(desiredStatus);
    if (!keys.length) {
      notifyError('没有可导入的站点（key 不匹配当前列表）');
      return;
    }
    if (Object.keys(results).length || Object.keys(errors).length) {
      try {
        await checkDashboardVideoSourceSites(results, errors);
      } catch (_e) {}
    }
    let orderKeys = [];
    if (order.length) {
      orderKeys = order.map((key) => (typeof key === 'string' ? key.trim() : '')).filter((key) => key && currentKeys.has(key));
    } else {
      desiredOrder.sort((a, b) => (a.order !== b.order ? a.order - b.order : a.idx - b.idx));
      orderKeys = desiredOrder.map((item) => item.key).filter(Boolean);
    }
    if (orderKeys.length) await saveDashboardVideoSourceOrder(orderKeys);
    const coverKey = (() => {
      const raw = typeof coverSite === 'string' ? coverSite.trim() : '';
      if (raw && currentKeys.has(raw)) return raw;
      return coverCandidates.length ? coverCandidates[0] : '';
    })();
    if (coverKey) {
      try { await updateDashboardVideoSourceCover(coverKey); } catch (_e) {}
    }
    for (const key of keys) {
      try { await updateDashboardVideoSourceStatus(key, !!desiredStatus[key]); } catch (_e) {}
      try { await updateDashboardVideoSourceHome(key, !!desiredHome[key]); } catch (_e) {}
      try { await updateDashboardVideoSourceSearch(key, !!desiredSearch[key]); } catch (_e) {}
    }
    await loadVideoPanel();
    notifySuccess(`导入成功（${keys.length} 个站点）`);
  } catch (err) {
    notifyError((err && err.message) || '导入失败');
  } finally {
    if (input) {
      try { input.value = ''; } catch (_e) {}
    }
  }
}

async function toggleVideoSourceEnabled(site, checked) {
  await persistVideoSourceToggle(site, 'enabled', checked, updateDashboardVideoSourceStatus);
}

async function toggleVideoSourceHome(site, checked) {
  await persistVideoSourceToggle(site, 'home', checked, updateDashboardVideoSourceHome);
}

async function toggleVideoSourceSearch(site, checked) {
  await persistVideoSourceToggle(site, 'search', checked, updateDashboardVideoSourceSearch);
}

async function toggleVideoSourceCover(site, checked) {
  const key = String(site && site.key || '').trim();
  if (!key || !checked) return;
  const prev = videoSourceCoverSite.value;
  videoSourceCoverSite.value = key;
  try {
    const data = await updateDashboardVideoSourceCover(key);
    videoSourceCoverSite.value = data && typeof data.coverSite === 'string' ? data.coverSite : key;
  } catch (err) {
    videoSourceCoverSite.value = prev;
    notifyError((err && err.message) || '保存失败');
  }
}

async function saveVideoSourceCurrentOrder() {
  try {
    await saveDashboardVideoSourceOrder(videoSourceSites.value.map((site) => site.key).filter(Boolean));
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  }
}

async function moveVideoSourceAndSave(index, direction) {
  moveVideoSource(index, direction);
  await saveVideoSourceCurrentOrder();
}

async function resetVideoSourceOrderFromCatpawrunner() {
  const apiBase = await resolveVideoCatApiBase();
  if (!apiBase) {
    notifyError('CatPawRunner 接口地址未设置');
    return;
  }
  try {
    const fullConfig = await fetchCatpawrunnerFullConfig(apiBase, props.bootstrap?.user?.username || '');
    const remote = fullConfig && fullConfig.video && Array.isArray(fullConfig.video.sites) ? fullConfig.video.sites : [];
    const remoteOrderKeys = remote.map((site) => (site && typeof site.key === 'string' ? site.key.trim() : '')).filter(Boolean);
    if (!remoteOrderKeys.length) {
      notifyError('未获取到排序');
      return;
    }
    const byKey = new Map(videoSourceSites.value.map((site) => [site.key, site]));
    const used = new Set();
    const next = [];
    remoteOrderKeys.forEach((key) => {
      const site = byKey.get(key);
      if (!site || used.has(key)) return;
      used.add(key);
      next.push(site);
    });
    videoSourceSites.value.forEach((site) => {
      if (!site.key || used.has(site.key)) return;
      used.add(site.key);
      next.push(site);
    });
    if (!next.length) {
      notifyError('未获取到站源');
      return;
    }
    videoSourceSites.value = next;
    await saveVideoSourceCurrentOrder();
    notifySuccess('排序已对齐');
  } catch (err) {
    notifyError((err && err.message) || '对齐失败');
  }
}

async function bulkToggleVideoSourceEnabled(enabled) {
  const keys = selectedVideoSourceKeys.value.filter(Boolean);
  if (!keys.length) return;
  await bulkUpdateVideoSourceFlags(keys, updateDashboardVideoSourceStatus, enabled);
  notifySuccess(enabled ? '批量启用完成' : '批量禁用完成');
}

async function bulkCheckAndDisableVideoSources() {
  const keys = selectedVideoSourceKeys.value.filter(Boolean);
  if (!keys.length) {
    return;
  }
  const apiBase = await resolveVideoCatApiBase();
  if (!apiBase) {
    notifyError('CatPawRunner 接口地址未设置');
    return;
  }
  try {
    const response = await probeDashboardVideoSourceSites({
      apiBase,
      keys,
      sites: videoSourceSites.value,
      tvUser: props.bootstrap?.user?.username || ''
    });
    if (Array.isArray(response && response.sites)) {
      applyVideoSourceSites(response.sites, videoSourceCoverSite.value);
    } else {
      await loadVideoPanel();
    }
    const invalidKeys = videoSourceSites.value.filter((site) => keys.includes(site.key) && site.availability === 'invalid').map((site) => site.key);
    await bulkUpdateVideoSourceFlags(invalidKeys, updateDashboardVideoSourceStatus, false);
    await bulkUpdateVideoSourceFlags(invalidKeys, updateDashboardVideoSourceHome, false);
    await bulkUpdateVideoSourceFlags(invalidKeys, updateDashboardVideoSourceSearch, false);
    await loadVideoPanel();
    notifySuccess('检测完成');
  } catch (err) {
    notifyError((err && err.message) || '检测失败');
  }
}

async function deleteCatServer() {
  if (!catSelectedServerKey.value || catDeleting.value) return;
  if (!window.confirm(`确定删除服务器：${catSelectedServerKey.value}？`)) return;
  catDeleting.value = true;
  try {
    const data = await deleteDashboardCatpawrunnerServer({ catpawrunnerServerKey: catSelectedServerKey.value });
    catServers.value = Array.isArray(data && data.servers)
      ? data.servers.map(normalizeCatServerRow).filter((item) => item.name)
      : [];
    catSelectedServerKey.value = (catServers.value[0] && catServers.value[0].name) || '';
    notifySuccess('删除成功');
    await reloadSelectedCatServerState();
  } catch (err) {
    notifyError((err && err.message) || '删除失败');
  } finally {
    catDeleting.value = false;
  }
}

async function syncCatPans() {
  if (!normalizedCatApiBase.value || catSyncPanBusy.value) return;
  catSyncPanBusy.value = true;
  try {
    const result = await syncPanLoginSettingsToCatpawrunner(
      normalizedCatApiBase.value,
      props.bootstrap?.user?.username || '',
      panSettingDefs
    );
    if (result && result.ok) {
      notifySuccess(`同步完成（成功 ${result.okCount || 0} 项）`);
    } else if (result && result.skipped) {
      notifyError('CatPawRunner 接口地址未设置');
    } else {
      notifyError('同步失败');
    }
  } catch (err) {
    notifyError((err && err.message) || '同步失败');
  } finally {
    catSyncPanBusy.value = false;
  }
}

function resetGoProxyEditorForm() {
  goProxyEditorForm.value = {
    name: '',
    displayName: '',
    base: '',
    pans: { baidu: true, quark: true }
  };
}

function buildGoProxyServerDraft() {
  return normalizeGoProxyServerRow(goProxyEditorForm.value);
}

function applyGoProxyServerDraft(draft) {
  const previousBase = goProxyEditorMode.value === 'edit' ? normalizeHttpBase(goProxyEditorOriginalBase.value) : '';
  if (goProxyEditorMode.value === 'edit' && goProxyEditorOriginalBase.value) {
    goProxyServers.value = goProxyServers.value.map((server) => (
      server.base === goProxyEditorOriginalBase.value ? draft : server
    ));
  } else {
    goProxyServers.value = [...goProxyServers.value.filter((server) => server.base !== draft.base), draft];
  }
  if (previousBase && previousBase !== draft.base && goProxyProbes.value[previousBase.toLowerCase()]) {
    const next = { ...goProxyProbes.value };
    delete next[previousBase.toLowerCase()];
    goProxyProbes.value = next;
  }
  ensureGoProxyProbeEntry(draft.base);
  probeOneGoProxyServer(draft.base);
}

function openGoProxyEditorForCreate() {
  goProxyEditorMode.value = 'create';
  goProxyEditorOriginalBase.value = '';
  resetGoProxyEditorForm();
  goProxyEditorOpen.value = true;
}

function openGoProxyEditorForEdit(server) {
  const row = normalizeGoProxyServerRow(server);
  goProxyEditorMode.value = 'edit';
  goProxyEditorOriginalBase.value = row.base;
  goProxyEditorForm.value = {
    name: row.name,
    displayName: row.displayName,
    base: row.base,
    pans: normalizeGoProxyPanMap(row.pans)
  };
  goProxyEditorOpen.value = true;
}

function closeGoProxyEditor() {
  goProxyEditorOpen.value = false;
  goProxyEditorOriginalBase.value = '';
  resetGoProxyEditorForm();
}

function confirmGoProxyEditor() {
  if (!canSaveGoProxyEditor.value) return;
  const draft = buildGoProxyServerDraft();
  applyGoProxyServerDraft(draft);
  closeGoProxyEditor();
}

function removeGoProxyServer(base) {
  const target = normalizeHttpBase(base);
  if (!target) return;
  goProxyServers.value = goProxyServers.value.filter((server) => server.base !== target);
  const key = target.toLowerCase();
  if (goProxyProbes.value[key]) {
    const next = { ...goProxyProbes.value };
    delete next[key];
    goProxyProbes.value = next;
  }
}

function setGoProxyPanEnabled(base, panKey, checked) {
  const target = normalizeHttpBase(base);
  if (!target) return;
  goProxyServers.value = goProxyServers.value.map((server) => {
    if (server.base !== target) return server;
    return {
      ...server,
      pans: {
        ...normalizeGoProxyPanMap(server.pans),
        [panKey]: !!checked
      }
    };
  });
}

async function probeOneGoProxyServer(base) {
  const target = normalizeHttpBase(base);
  if (!target) return;
  const prev = getGoProxyProbe(target);
  setGoProxyProbe(target, { state: 'checking', version: '', checkedAt: prev.checkedAt || 0 });
  try {
    const result = await probeGoProxyVersion(target, 4000);
    setGoProxyProbe(target, { state: 'online', version: result.version || '', checkedAt: Date.now() });
  } catch (_e) {
    setGoProxyProbe(target, { state: 'offline', version: '', checkedAt: Date.now() });
  }
}

async function probeAllGoProxyServers() {
  const targets = goProxyServers.value.map((server) => server.base).filter(Boolean);
  targets.forEach((base) => ensureGoProxyProbeEntry(base));
  await Promise.all(targets.map((base) => probeOneGoProxyServer(base)));
}

async function saveGoProxySettings() {
  if (goProxySaving.value) return;
  goProxySaving.value = true;
  try {
    await saveDashboardGoProxySettings({
      goProxyEnabled: goProxyForm.value.enabled ? '1' : '0',
      goProxyAutoSelect: goProxyForm.value.autoSelect ? '1' : '0',
      goProxyServersJson: JSON.stringify(goProxyServers.value)
    });
    notifySuccess('保存成功');
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    goProxySaving.value = false;
  }
}

function resetRelayEditorForm() {
  relayEditorForm.value = {
    name: '',
    displayName: '',
    base: '',
    secret: ''
  };
}

function buildRelayServerDraft() {
  return normalizeRelayServerRow(relayEditorForm.value);
}

function applyRelayServerDraft(draft) {
  const previousBase = relayEditorMode.value === 'edit' ? normalizeHttpBase(relayEditorOriginalBase.value) : '';
  if (relayEditorMode.value === 'edit' && relayEditorOriginalBase.value) {
    relayServers.value = relayServers.value.map((server) => (
      server.base === relayEditorOriginalBase.value ? draft : server
    ));
  } else {
    relayServers.value = [...relayServers.value.filter((server) => server.base !== draft.base), draft];
  }
  if (previousBase && previousBase !== draft.base && relayProbes.value[previousBase.toLowerCase()]) {
    const next = { ...relayProbes.value };
    delete next[previousBase.toLowerCase()];
    relayProbes.value = next;
  }
  ensureRelayProbeEntry(draft.base);
  probeOneRelayServer(draft.base);
}

function openRelayEditorForCreate() {
  relayEditorMode.value = 'create';
  relayEditorOriginalBase.value = '';
  resetRelayEditorForm();
  relayEditorOpen.value = true;
}

function openRelayEditorForEdit(server) {
  const row = normalizeRelayServerRow(server);
  relayEditorMode.value = 'edit';
  relayEditorOriginalBase.value = row.base;
  relayEditorForm.value = {
    name: row.name,
    displayName: row.displayName,
    base: row.base,
    secret: row.secret
  };
  relayEditorOpen.value = true;
}

function closeRelayEditor() {
  relayEditorOpen.value = false;
  relayEditorOriginalBase.value = '';
  resetRelayEditorForm();
}

function confirmRelayEditor() {
  if (!canSaveRelayEditor.value) return;
  const draft = buildRelayServerDraft();
  applyRelayServerDraft(draft);
  closeRelayEditor();
}

function removeRelayServer(base) {
  const target = normalizeHttpBase(base);
  if (!target) return;
  relayServers.value = relayServers.value.filter((server) => server.base !== target);
  const key = target.toLowerCase();
  if (relayProbes.value[key]) {
    const next = { ...relayProbes.value };
    delete next[key];
    relayProbes.value = next;
  }
}

async function probeOneRelayServer(base) {
  const target = normalizeHttpBase(base);
  if (!target) return;
  const row = relayServers.value.find((server) => server.base === target);
  const secret = row && typeof row.secret === 'string' ? row.secret.trim() : '';
  const prev = getRelayProbe(target);
  setRelayProbe(target, { state: 'checking', version: '', checkedAt: prev.checkedAt || 0 });
  try {
    const result = await probeRelayVersion(target, secret, 4000);
    setRelayProbe(target, { state: 'online', version: result.version || '', checkedAt: Date.now() });
  } catch (_e) {
    setRelayProbe(target, { state: 'offline', version: '', checkedAt: Date.now() });
  }
}

async function probeAllRelayServers() {
  const targets = relayServers.value.map((server) => server.base).filter(Boolean);
  targets.forEach((base) => ensureRelayProbeEntry(base));
  await Promise.all(targets.map((base) => probeOneRelayServer(base)));
}

async function saveRelaySettings() {
  if (relaySaving.value) return;
  relaySaving.value = true;
  try {
    await saveDashboardRelaySettings({
      relayEnabled: relayForm.value.enabled ? '1' : '0',
      auth: relayForm.value.auth || '',
      relayServersJson: JSON.stringify(relayServers.value)
    });
    notifySuccess('保存成功');
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    relaySaving.value = false;
  }
}

async function runBusyTask(flagRef, task, successMessage, fallbackErrorMessage, infoMessage = '') {
  if (flagRef.value) return false;
  flagRef.value = true;
  if (infoMessage) notifyInfo(infoMessage);
  try {
    await task();
    if (successMessage) notifySuccess(successMessage);
    return true;
  } catch (err) {
    notifyError((err && err.message) || fallbackErrorMessage);
    return false;
  } finally {
    flagRef.value = false;
  }
}

async function runUserRowAction(username, action, successMessage, fallbackErrorMessage) {
  if (!username || userRowBusy.value) return false;
  userRowBusy.value = username;
  try {
    await action();
    notifySuccess(typeof successMessage === 'function' ? successMessage() : successMessage);
    return true;
  } catch (err) {
    notifyError((err && err.message) || fallbackErrorMessage);
    return false;
  } finally {
    userRowBusy.value = '';
  }
}

function replaceUserRow(previousUsername, nextUser) {
  const normalized = normalizeUserRow(nextUser);
  users.value = users.value.map((user) => (user.username === previousUsername ? normalized : user));
}

function toggleAddUserForm() {
  addUserFormOpen.value = !addUserFormOpen.value;
  if (!addUserFormOpen.value) {
    addUserForm.value.username = '';
    addUserForm.value.password = '';
  }
}

function openEditUser(user) {
  const username = user && typeof user.username === 'string' ? user.username : '';
  if (!username) return;
  if (editingUsername.value === username) {
    closeEditUser();
    return;
  }
  editingUsername.value = username;
  editUserForm.value.newUsername = '';
  editUserForm.value.newPassword = '';
}

function closeEditUser() {
  editingUsername.value = '';
  editUserForm.value.newUsername = '';
  editUserForm.value.newPassword = '';
}

async function loadUsersPanel() {
  if (!isAdmin.value || userLoading.value) return;
  userLoading.value = true;
  try {
    const data = await fetchUsers();
    users.value = Array.isArray(data.users) ? data.users.map(normalizeUserRow) : [];
  } catch (err) {
    users.value = [];
    notifyError((err && err.message) || '加载失败');
  } finally {
    userLoading.value = false;
  }
}

async function loadSitePanel() {
  if (!isAdmin.value || siteLoading.value) return;
  siteLoading.value = true;
  try {
    const settings = await fetchSiteSettings();
    siteForm.value.siteName = typeof settings.siteName === 'string' ? settings.siteName : props.bootstrap.siteName || '';
    siteForm.value.searchDisplayMode = typeof settings.searchDisplayMode === 'string' ? settings.searchDisplayMode : 'sites';
    siteForm.value.netdiskProxyEnabled = !!settings.netdiskProxyEnabled;
    siteForm.value.netdiskProxyUrl = typeof settings.netdiskProxyUrl === 'string' ? settings.netdiskProxyUrl : '';
    const validation = await validateSearchDisplayMode(siteForm.value.searchDisplayMode);
    if (!validation.valid) {
      siteForm.value.searchDisplayMode = 'sites';
      searchDisplayModeError.value = validation.message || 'TMDB API TOKEN 未设置';
    } else {
      searchDisplayModeError.value = '';
    }
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    siteLoading.value = false;
  }
}

async function loadThirdpartyPanel() {
  if (!isAdmin.value || thirdPartyLoading.value || thirdPartyLoaded.value) return;
  thirdPartyLoading.value = true;
  closeThirdPartyDropdown();
  try {
    await loadThirdPartyHomeSites();
    const data = await fetchThirdpartySettings();
    const sections = Array.isArray(data && data.thirdPartyClientHomeSections)
      ? data.thirdPartyClientHomeSections
      : (Array.isArray(data && data.embyHomeSections) ? data.embyHomeSections : []);
    applyThirdPartyHomeSections(sections);
    const siteKeys = Array.from(
      new Set(
        thirdPartyHomeSections.value
          .filter((row) => row && isThirdPartyModuleRequiringSite(row.module))
          .map((row) => String(row.siteKey || '').trim())
          .filter(Boolean)
      )
    );
    for (const siteKey of siteKeys) {
      await ensureThirdPartySiteCategoriesLoaded(siteKey);
    }
    thirdPartyLoaded.value = true;
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    thirdPartyLoading.value = false;
  }
}

async function saveThirdpartyPanel() {
  if (!isAdmin.value || thirdPartyLoading.value || thirdPartySaving.value) return;
  const normalized = thirdPartyHomeSections.value.map(normalizeThirdPartyHomeSection).filter(Boolean);
  const error = validateThirdPartyHomeSections(normalized);
  if (error) {
    notifyError(error);
    return;
  }
  thirdPartySaving.value = true;
  closeThirdPartyDropdown();
  try {
    await saveThirdpartySettings({ thirdPartyClientHomeSections: normalized });
    applyThirdPartyHomeSections(normalized);
    notifySuccess('保存成功');
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    thirdPartySaving.value = false;
  }
}

function applyMetadataSettings(settings) {
  const root = settings && typeof settings === 'object' ? settings : {};
  metadataForm.value = {
    doubanDataProxy: typeof root.doubanDataProxy === 'string' ? root.doubanDataProxy : 'server-proxy',
    doubanDataCustom: typeof root.doubanDataCustom === 'string' ? root.doubanDataCustom : '',
    doubanImgProxy: typeof root.doubanImgProxy === 'string' ? root.doubanImgProxy : 'server-proxy',
    doubanImgCustom: typeof root.doubanImgCustom === 'string' ? root.doubanImgCustom : '',
    doubanSearchCookie: typeof root.doubanSearchCookie === 'string' ? root.doubanSearchCookie : '',
    tmdbApiToken: typeof root.tmdbApiToken === 'string' ? root.tmdbApiToken : '',
    tmdbDataProxyBase: typeof root.tmdbDataProxyBase === 'string' ? root.tmdbDataProxyBase : '',
    tmdbImageProxyBase: typeof root.tmdbImageProxyBase === 'string' ? root.tmdbImageProxyBase : '',
    language: typeof root.language === 'string' && root.language.trim() ? root.language : 'zh-CN',
    region: typeof root.region === 'string' && root.region.trim() ? root.region : 'CN',
    includeAdult: !!root.includeAdult
  };
}

async function loadMetadataPanel() {
  if (!isAdmin.value || metadataLoading.value) return;
  metadataLoading.value = true;
  try {
    const data = await fetchMetadataSettings();
    applyMetadataSettings(data);
  } catch (err) {
    notifyError((err && err.message) || '加载失败');
  } finally {
    metadataLoading.value = false;
  }
}

function resolvePersistedDashboardNavKey() {
  if (!isAdmin.value || typeof window === 'undefined' || !window.localStorage) return 'site';
  try {
    const raw = String(window.localStorage.getItem(DASHBOARD_ACTIVE_NAV_STORAGE_KEY) || '').trim();
    if (adminNavItems.some((item) => item.key === raw)) return raw;
  } catch (_error) {}
  return 'site';
}

function persistDashboardNavKey(key) {
  if (!isAdmin.value || typeof window === 'undefined' || !window.localStorage) return;
  try {
    window.localStorage.setItem(DASHBOARD_ACTIVE_NAV_STORAGE_KEY, key);
  } catch (_error) {}
}

function loadDashboardPanelByKey(key) {
  if (key === 'site') {
    void loadSitePanel();
    return;
  }
  if (key === 'user') {
    void loadUsersPanel();
    return;
  }
  if (key === 'pan') {
    void loadPanPanel();
    return;
  }
  if (key === 'interface') {
    void loadInterfacePanel();
    return;
  }
  if (key === 'video') {
    void loadVideoPanel();
    return;
  }
  if (key === 'magic') {
    void loadMagicPanel();
    return;
  }
  if (key === 'smart') {
    void loadSmartPanel();
    return;
  }
  if (key === 'thirdparty') {
    void loadThirdpartyPanel();
    return;
  }
  if (key === 'metadata') {
    void loadMetadataPanel();
  }
}

async function saveMetadataPanel() {
  if (!isAdmin.value || metadataLoading.value || metadataSaving.value) return;
  metadataSaving.value = true;
  try {
    const data = await saveMetadataSettings({
      doubanDataProxy: metadataForm.value.doubanDataProxy,
      doubanDataCustom: metadataForm.value.doubanDataCustom,
      doubanImgProxy: metadataForm.value.doubanImgProxy,
      doubanImgCustom: metadataForm.value.doubanImgCustom,
      doubanSearchCookie: metadataForm.value.doubanSearchCookie,
      tmdbApiToken: metadataForm.value.tmdbApiToken,
      tmdbDataProxyBase: metadataForm.value.tmdbDataProxyBase,
      tmdbImageProxyBase: metadataForm.value.tmdbImageProxyBase,
      language: metadataForm.value.language,
      region: metadataForm.value.region,
      includeAdult: !!metadataForm.value.includeAdult
    });
    applyMetadataSettings(data);
    if (siteForm.value.searchDisplayMode !== 'sites') {
      const validation = await validateSearchDisplayMode(siteForm.value.searchDisplayMode);
      if (!validation.valid) {
        siteForm.value.searchDisplayMode = 'sites';
        searchDisplayModeError.value = validation.message || 'TMDB API TOKEN 未设置';
      } else {
        searchDisplayModeError.value = '';
      }
    }
    notifySuccess('保存成功');
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    metadataSaving.value = false;
  }
}

function beginMetadataCacheClearConfirm(scope) {
  const key = typeof scope === 'string' ? scope.trim().toLowerCase() : '';
  if (key !== 'douban' && key !== 'tmdb' && key !== 'all') return;
  if (metadataPanelBusy.value || metadataCacheClearingKey.value) return;
  metadataCacheConfirmingKey.value = key;
}

function cancelMetadataCacheClearConfirm() {
  if (metadataCacheClearingKey.value) return;
  metadataCacheConfirmingKey.value = '';
}

async function confirmMetadataCacheClear(scope) {
  const key = typeof scope === 'string' ? scope.trim().toLowerCase() : '';
  if (key !== 'douban' && key !== 'tmdb' && key !== 'all') return;
  if (!isAdmin.value || metadataPanelBusy.value || metadataCacheClearingKey.value || metadataCacheConfirmingKey.value !== key) return;
  metadataCacheClearingKey.value = key;
  try {
    if (key === 'douban') {
      await clearDoubanMetadataCache();
    } else if (key === 'tmdb') {
      await clearTMDBMetadataCache();
    } else {
      await clearAllMetadataCache();
    }
    notifySuccess(`已清除${metadataCacheActionLabels[key] || '缓存'}`);
    metadataCacheConfirmingKey.value = '';
  } catch (err) {
    notifyError((err && err.message) || '缓存清理失败');
  } finally {
    metadataCacheClearingKey.value = '';
  }
}

async function saveGlobalSettings() {
  if (!isAdmin.value) return;
  await runBusyTask(
    globalSaving,
    async () => {
      const validation = await validateSearchDisplayMode(siteForm.value.searchDisplayMode);
      if (!validation.valid) {
        siteForm.value.searchDisplayMode = 'sites';
        searchDisplayModeError.value = validation.message || 'TMDB API TOKEN 未设置';
        throw new Error(searchDisplayModeError.value);
      }
      searchDisplayModeError.value = '';
      await saveGlobalSiteSettings({
        siteName: siteForm.value.siteName,
        searchDisplayMode: siteForm.value.searchDisplayMode,
        netdiskProxyEnabled: siteForm.value.netdiskProxyEnabled ? '1' : '0',
        netdiskProxyUrl: siteForm.value.netdiskProxyUrl
      });
    },
    '保存成功',
    '保存失败'
  );
}

async function exportBackup() {
  if (backupImporting.value) return;
  await runBusyTask(
    backupExporting,
    async () => {
      const data = await exportDashboardBackup();
      downloadJsonFile(data, buildBackupFilename());
    },
    '导出成功',
    '导出失败',
    '正在导出...'
  );
}

function pickImportFile() {
  if (!backupImportFileRef.value) return;
  backupImportFileRef.value.value = '';
  backupImportFileRef.value.click();
}

async function refreshDashboardStateAfterBackupImport() {
  await loadSitePanel();
  if (activeNavKey.value === 'interface') {
    await loadInterfacePanel();
    return;
  }
  if (activeNavKey.value === 'pan') {
    loadedPanSettingKeys.clear();
    await loadPanPanel();
    return;
  }
  if (activeNavKey.value === 'video') {
    await loadVideoPanel();
    return;
  }
  if (activeNavKey.value === 'magic') {
    await loadMagicPanel();
    return;
  }
  if (activeNavKey.value === 'smart') {
    smartLoaded.value = false;
    await loadSmartPanel(true);
    return;
  }
  if (activeNavKey.value === 'thirdparty') {
    thirdPartyLoaded.value = false;
    await loadThirdpartyPanel();
    return;
  }
  if (activeNavKey.value === 'metadata') {
    await loadMetadataPanel();
  }
}

async function importBackup(event) {
  const input = event && event.target ? event.target : null;
  const file = input && input.files && input.files[0] ? input.files[0] : null;
  if (!file) return;
  await runBusyTask(
    backupImporting,
    async () => {
      const text = await file.text();
      const parsed = JSON.parse(text || '{}');
      await restoreDashboardBackup(parsed);
      await refreshDashboardStateAfterBackupImport();
    },
    '导入成功',
    '导入失败',
    '正在导入...'
  );
  if (input) input.value = '';
}

async function submitAddUser() {
  if (!isAdmin.value || userAddSubmitting.value || !canSubmitAddUser.value) return;
  userAddSubmitting.value = true;
  try {
    const username = addUserForm.value.username.trim();
    const password = addUserForm.value.password.trim();
    await addDashboardUser({ username, password });
    users.value = [...users.value, normalizeUserRow({ username, role: 'user', status: 'active' })];
    addUserForm.value.username = '';
    addUserForm.value.password = '';
    addUserFormOpen.value = false;
    notifySuccess('添加成功');
  } catch (err) {
    notifyError((err && err.message) || '添加失败');
  } finally {
    userAddSubmitting.value = false;
  }
}

async function submitEditUser() {
  if (!editingUsername.value || userEditSubmitting.value || !canSubmitUserEdit.value) return;
  userEditSubmitting.value = true;
  const username = editingUsername.value;
  try {
    const payload = { username };
    if (editUserForm.value.newUsername.trim()) payload.newUsername = editUserForm.value.newUsername.trim();
    if (editUserForm.value.newPassword.trim()) payload.newPassword = editUserForm.value.newPassword.trim();
    const data = await updateDashboardUser(payload);
    const current = users.value.find((user) => user.username === username);
    replaceUserRow(username, {
      username: data.username || username,
      role: data.role || (current && current.role) || 'user',
      status: (current && current.status) || 'active'
    });
    notifySuccess('保存成功');
    closeEditUser();
  } catch (err) {
    notifyError((err && err.message) || '保存失败');
  } finally {
    userEditSubmitting.value = false;
  }
}

async function toggleUserStatus(user) {
  const username = user && typeof user.username === 'string' ? user.username : '';
  await runUserRowAction(
    username,
    async () => {
      const data = await toggleDashboardUserStatus({ username });
      replaceUserRow(username, { ...user, status: data.status || user.status });
    },
    () => {
      const current = users.value.find((item) => item.username === username);
      return (current?.status || user.status) === 'active' ? '已解封' : '已封禁';
    },
    '操作失败'
  );
}

async function removeUser(user) {
  const username = user && typeof user.username === 'string' ? user.username : '';
  if (!username) return;
  if (!window.confirm(`确定删除用户：${username}？`)) return;
  await runUserRowAction(
    username,
    async () => {
      await deleteDashboardUser({ username });
      users.value = users.value.filter((item) => item.username !== username);
      if (editingUsername.value === username) closeEditUser();
    },
    '已删除',
    '删除失败'
  );
}

onMounted(() => {
  activeNavKey.value = resolvePersistedDashboardNavKey();
  loadDashboardPanelByKey(activeNavKey.value);
  document.addEventListener('click', onDocumentClick);
  window.addEventListener('tv:smart-matchblock-updated', handleSmartMatchBlockUpdated);
});

onBeforeUnmount(() => {
  document.removeEventListener('click', onDocumentClick);
  window.removeEventListener('tv:smart-matchblock-updated', handleSmartMatchBlockUpdated);
  stopAllQrPolling();
});

watch(activeNavKey, (key) => {
  persistDashboardNavKey(key);
  loadDashboardPanelByKey(key);
});

watch(manualTmdbSelectedKey, (key) => {
  manualItemEditorOpen.value = false;
  manualItemDraft.value = buildManualItemDraft();
  cancelManualItemRowEditor();
  if (!key) {
    manualItemRows.value = [];
    return;
  }
  void loadManualItemRows();
});

watch([showCatSyncFromServerRow, catSyncFromServerOptions], ([visible, options]) => {
  if (!visible) {
    if (catSyncFromServerKey.value) catSyncFromServerKey.value = '';
    return;
  }
  const list = Array.isArray(options) ? options : [];
  const valid = list.some((item) => item && item.key === catSyncFromServerKey.value);
  if (!valid) catSyncFromServerKey.value = '';
});
</script>

<style>
@import '../../shared/commonButtons.css';
@import '../../shared/commonFields.css';

.hidden { display: none !important; }

.adm-min-h-screen { min-height: 100vh; }
.adm-min-h-40 { min-height: 40px; }
.adm-flex { display: flex; }
.adm-flex-col { flex-direction: column; }
.adm-flex-1 { flex: 1 1 0%; }
.adm-flex-shrink-0 { flex-shrink: 0; }
.adm-flex-wrap { flex-wrap: wrap; }
.adm-grid { display: grid; }
.adm-items-center { align-items: center; }
.adm-justify-between { justify-content: space-between; }
.adm-justify-start { justify-content: flex-start; }
.adm-gap-2 { gap: 0.5rem; }
.adm-gap-3 { gap: 0.75rem; }
.adm-space-y-1 > * + * { margin-top: 0.25rem; }
.adm-space-y-3 > * + * { margin-top: 0.75rem; }
.adm-space-y-4 > * + * { margin-top: 1rem; }
.adm-space-y-6 > * + * { margin-top: 1.5rem; }
.adm-mb-1 { margin-bottom: 0.25rem; }
.adm-mb-2 { margin-bottom: 0.5rem; }
.adm-mb-6 { margin-bottom: 1.5rem; }
.adm-mt-1 { margin-top: 0.25rem; }
.adm-mt-6 { margin-top: 1.5rem; }
.adm-pt-1 { padding-top: 0.25rem; }
.adm-pt-2 { padding-top: 0.5rem; }
.adm-pt-6 { padding-top: 1.5rem; }
.adm-pb-1 { padding-bottom: 0.25rem; }
.adm-p-3 { padding: 0.75rem; }
.adm-p-4 { padding: 1rem; }
.adm-p-6 { padding: 1.5rem; }
.adm-px-3 { padding-left: 0.75rem; padding-right: 0.75rem; }
.adm-px-4 { padding-left: 1rem; padding-right: 1rem; }
.adm-py-2 { padding-top: 0.5rem; padding-bottom: 0.5rem; }
.adm-py-3 { padding-top: 0.75rem; padding-bottom: 0.75rem; }
.adm-pl-4 { padding-left: 1rem; }
.adm-block { display: block; }
.adm-relative { position: relative; }
.adm-overflow-hidden { overflow: hidden; }
.adm-overflow-x-auto { overflow-x: auto; }
.adm-overflow-y-auto { overflow-y: auto; }
.adm-sticky { position: sticky; }
.adm-top-0 { top: 0; }
.adm-z-2 { z-index: 2; }
.adm-min-w-0 { min-width: 0; }
.adm-whitespace-nowrap { white-space: nowrap; }
.adm-w-max { width: max-content; }
.adm-max-w-full { max-width: 100%; }
.adm-w-5 { width: 1.25rem; }
.adm-h-5 { height: 1.25rem; }
.adm-w-64 { width: 16rem; }
.adm-w-full { width: 100%; }
.adm-max-h-60vh { max-height: 60vh; }
.adm-table-fixed { table-layout: fixed; }
.adm-rounded-full { border-radius: 9999px; }
.adm-rounded-lg { border-radius: 0.5rem; }
.adm-border { border-width: 1px; border-style: solid; border-color: #e5e7eb; }
.adm-border-b { border-bottom-width: 1px; border-bottom-style: solid; }
.adm-border-r { border-right-width: 1px; border-right-style: solid; }
.adm-border-gray-100 { border-color: #f3f4f6; }
.adm-border-gray-200-50 { border-color: rgba(229, 231, 235, 0.5); }
.adm-bg-white-40 { background: rgba(255, 255, 255, 0.4); }
.adm-shadow-lg {
  box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.1),
    0 4px 6px -4px rgba(0, 0, 0, 0.1);
}
.adm-backdrop-blur-xl {
  backdrop-filter: blur(24px);
  -webkit-backdrop-filter: blur(24px);
}
.adm-transition-all { transition-property: all; }
.adm-transition-colors { transition: color 0.15s ease, background-color 0.15s ease, border-color 0.15s ease; }
.adm-duration-200 { transition-duration: 200ms; }
.adm-duration-300 { transition-duration: 300ms; }
.adm-text-xs { font-size: 0.75rem; line-height: 1rem; }
.adm-text-sm { font-size: 0.875rem; line-height: 1.25rem; }
.adm-text-base { font-size: 1rem; line-height: 1.5rem; }
.adm-text-xl { font-size: 1.25rem; line-height: 1.75rem; }
.adm-font-medium { font-weight: 500; }
.adm-font-semibold { font-weight: 600; }
.adm-font-bold { font-weight: 700; }
.adm-text-left { text-align: left; }
.adm-text-gray-500 { color: #6b7280; }
.adm-text-gray-600 { color: #4b5563; }
.adm-text-gray-700 { color: #374151; }
.adm-text-gray-800 { color: #1f2937; }
.adm-text-green-600 { color: #16a34a; }
.adm-text-red-600 { color: #dc2626; }

.adm-divide-gray-100 { --adm-divide-color: #f3f4f6; }
.adm-divide-x > * + * { border-left: 1px solid #f3f4f6; }
.adm-divide-y > * + * { border-top: 1px solid #f3f4f6; }
.adm-divide-x.adm-divide-gray-100 > * + * { border-left-color: var(--adm-divide-color); }
.adm-divide-y.adm-divide-gray-100 > * + * { border-top-color: var(--adm-divide-color); }
.adm-hover-bg-gray-100-30:hover { background: rgba(243, 244, 246, 0.3); }
.adm-hover-text-green-600:hover { color: #16a34a; }
.nav-item:hover .adm-group-hover-text-green-600 { color: #16a34a; }
.adm-data-active-bg-green-500-20[data-active='true'] { background: rgba(34, 197, 94, 0.2); }
.adm-data-active-text-green-700[data-active='true'] { color: #15803d; }
.dashboard-nav[data-active='true'] svg { color: #15803d; }

.sidebar { overflow: hidden; }
.dashboard-sidebar {
  position: sticky;
  top: 0;
  height: 100vh;
  max-height: 100vh;
  align-self: flex-start;
  overflow-y: auto;
}

.nav-item {
  width: 100%;
  border: 0;
  background: transparent;
  text-align: left;
}

.dashboard-panel {
  min-height: calc(100vh - 3rem);
}

.dashboard-card {
  background: #ffffff;
  border: 1px solid #e5e7eb;
  border-radius: 12px;
  padding: 16px;
  box-shadow: 0 6px 12px rgba(0, 0, 0, 0.05);
}


.stat-card {
  display: inline-flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 4px;
  min-width: 112px;
  padding: 14px 16px;
  border-radius: 14px;
  border: 1px solid #dcfce7;
  background: linear-gradient(180deg, #f0fdf4 0%, #dcfce7 100%);
  box-shadow: 0 6px 12px rgba(34, 197, 94, 0.08);
}

.stat-main {
  font-size: 1.75rem;
  line-height: 1;
  font-weight: 700;
  color: #166534;
}

.stat-label {
  font-size: 0.75rem;
  line-height: 1rem;
  color: #4b5563;
}

.btn-pill-small {
  padding: 4px 8px;
  border-radius: 9999px;
  font-size: 12px;
  line-height: 1rem;
  font-weight: 500;
}

.btn-pill-green {
  appearance: none;
  -webkit-appearance: none;
  border: 1px solid #22c55e;
  background: rgba(34, 197, 94, 0.1);
  color: #16a34a;
  cursor: pointer;
  transition: all 0.2s ease;
}

.btn-pill-green:hover {
  background: rgba(34, 197, 94, 0.16);
}

.btn-pill-blue {
  appearance: none;
  -webkit-appearance: none;
  border: 1px solid rgba(96, 165, 250, 0.55);
  background: rgba(96, 165, 250, 0.1);
  color: #2563eb;
  cursor: pointer;
  transition: all 0.2s ease;
}

.btn-pill-blue:hover {
  background: rgba(96, 165, 250, 0.16);
  border-color: rgba(96, 165, 250, 0.75);
}

.btn-pill-red {
  appearance: none;
  -webkit-appearance: none;
  border: 1px solid #ef4444;
  background: rgba(239, 68, 68, 0.08);
  color: #dc2626;
  cursor: pointer;
  transition: all 0.2s ease;
}

.btn-pill-red:hover {
  background: rgba(239, 68, 68, 0.12);
}

.btn-ghost-red {
  padding: 8px 12px;
  border-radius: 10px;
  appearance: none;
  -webkit-appearance: none;
  background: transparent;
  border: 1px solid rgba(248, 113, 113, 0.7);
  color: #ef4444;
  font-size: 14px;
  font-weight: 600;
  transition: all 0.2s ease;
  cursor: pointer;
}

.btn-ghost-red:hover {
  background: rgba(248, 113, 113, 0.08);
  border-color: rgba(239, 68, 68, 0.75);
}

.btn-ghost-red:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.tv-panel {
  border: 1px solid #e5e7eb;
  border-radius: 12px;
  background: #ffffff;
  padding: 12px;
}

.tv-us-acc-item {
  border: 1px solid #e5e7eb;
  border-radius: 12px;
  background: #ffffff;
  overflow: hidden;
}

.tv-us-acc-head {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 14px;
  border: 0;
  background: transparent;
  text-align: left;
  font-size: 14px;
  font-weight: 600;
  color: #374151;
  cursor: pointer;
}

.tv-us-acc-icon {
  color: #6b7280;
  transition: transform 0.2s ease;
}

.tv-us-acc-icon[data-open='true'] {
  transform: rotate(180deg);
}

.tv-us-acc-body {
  padding: 0 14px 14px;
}

.tv-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  border-radius: 0.5rem;
  border: 1px solid #e5e7eb;
  background: #f9fafb;
  padding: 0.5rem 0.75rem;
}

.pan-list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.adm-space-y-2 > :not([hidden]) ~ :not([hidden]) {
  --tw-space-y-reverse: 0;
  margin-top: calc(0.5rem * calc(1 - var(--tw-space-y-reverse)));
  margin-bottom: calc(0.5rem * var(--tw-space-y-reverse));
}

.video-source-checkbox {
  width: 1rem;
  height: 1rem;
  flex: 0 0 auto;
}

.sort-btn-group {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.sort-btn {
  appearance: none;
  -webkit-appearance: none;
  width: 32px;
  height: 32px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 10px;
  border: 1px solid #e5e7eb;
  background: #ffffff;
  color: #4b5563;
  cursor: pointer;
  transition: all 0.15s ease;
}

.sort-btn:hover {
  background: #f8fafc;
  border-color: #d1d5db;
}

.sort-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.tv-cpo-server-select {
  display: inline-flex;
  align-items: center;
}

.tv-cpo-server-select.ui-selectbox {
  width: auto;
  display: inline-block;
  min-width: 6em;
}

.tv-cpo-server-select .ui-selectbox__trigger {
  position: relative;
  width: 100%;
  min-width: 6em;
  padding-right: 34px;
  white-space: nowrap;
  text-align: center;
  overflow: visible;
  text-overflow: clip;
}

.tv-cpo-server-select .ui-selectbox__trigger::after {
  content: '';
  position: absolute;
  right: 10px;
  top: 50%;
  width: 12px;
  height: 16px;
  background-repeat: no-repeat;
  background-position: center;
  background-size: 12px 16px;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='16' viewBox='0 0 12 16' fill='none'%3E%3Cpath d='M3 6L6 3L9 6' stroke='%2364748b' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'/%3E%3Cpath d='M3 10L6 13L9 10' stroke='%2364748b' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");
  transform: translateY(-50%);
  opacity: 0.72;
  pointer-events: none;
}

.tv-cpo-server-select .ui-selectbox__menu {
  width: 100%;
  min-width: 6em;
}

.tv-cpo-server-select .ui-selectbox__option {
  white-space: nowrap;
}

.tv-cpo-config-shell {
  display: inline-block;
  max-width: 100%;
}

.tv-cpo-config-table {
  display: inline-block;
  width: max-content;
  max-width: 100%;
}

.tv-cpo-config-table table {
  border-collapse: collapse;
}

.tv-cpo-config-table tbody tr + tr {
  border-top: 1px solid #f3f4f6;
}

.smart-match-head-grid {
  display: grid;
  grid-template-columns: 72px 160px 280px 96px 160px 220px 88px;
  width: 1076px;
  min-width: 1076px;
}

.smart-match-table {
  table-layout: fixed;
  width: 1076px;
  min-width: 1076px;
}

.smart-manual-head-table {
  border-collapse: collapse;
}

.smart-manual-table {
  table-layout: fixed;
  width: calc(672px + var(--manual-col-detail, 220px) + var(--manual-col-panflag, 120px));
  min-width: calc(672px + var(--manual-col-detail, 220px) + var(--manual-col-panflag, 120px));
}

.smart-match-keyword-chip {
  width: 100%;
  justify-content: space-between;
}

.smart-match-keyword-main {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
  flex: 1 1 auto;
}

.smart-match-keyword-name {
  max-width: 9rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.smart-match-keyword-count {
  width: 1.5rem;
  height: 1.5rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 9999px;
  background: rgba(107, 114, 128, 0.10);
  color: #4b5563;
  font-size: 12px;
  line-height: 1;
  flex: 0 0 auto;
}

.tv-cpo-config-editor {
  width: 100%;
  max-width: 100%;
  box-sizing: border-box;
}

.tv-cpo-online-config-editor .user-form-grid {
  align-items: center;
}

.tv-cpo-online-config-editor .user-form-grid__label {
  display: inline-flex;
  align-items: center;
  min-height: 36px;
}

.tv-cpo-online-config-editor__actions {
  margin-top: 16px;
}

.magic-rule-list {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.magic-rule-add-row {
  margin-bottom: 12px;
}

.magic-rule-row {
  display: flex;
  justify-content: flex-start;
}

.magic-rule-shell {
  display: flex;
  align-items: center;
  gap: 12px;
  width: min(100%, var(--magic-rule-max-width, 1100px));
  min-width: min(100%, var(--magic-rule-min-width, 780px));
  padding: 8px 12px;
  border: 1px solid rgba(203, 213, 225, 0.85);
  border-radius: 10px;
  background: rgba(248, 250, 252, 0.72);
  box-sizing: border-box;
}

.magic-rule-list--single .magic-rule-shell {
  --magic-rule-min-width: 760px;
  --magic-rule-max-width: 1080px;
}

.magic-rule-list--pair .magic-rule-shell {
  --magic-rule-min-width: 920px;
  --magic-rule-max-width: 1120px;
}

.magic-rule-index {
  flex: 0 0 24px;
  color: #475569;
  font-size: 14px;
  line-height: 1;
  text-align: right;
}

.magic-rule-single {
  flex: 1 1 auto;
  min-width: 0;
}

.magic-rule-pair {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1 1 auto;
  min-width: 0;
}

.magic-rule-pair--add {
  flex: 0 1 auto;
  width: min(100%, 1120px);
  min-width: min(100%, 920px);
}

.smart-alias-add-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: nowrap;
  min-width: 0;
}

.smart-alias-add-pair {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 0 1 auto;
  width: min(100%, 1120px);
  min-width: min(100%, 920px);
}

.smart-alias-add-pair > .tv-field:first-child {
  flex: 1 1 0;
}

.smart-alias-add-pair > .tv-field:last-child {
  flex: 2 1 0;
}

.magic-rule-preview {
  margin: 6px 0 0 36px;
  color: #16a34a;
  font-size: 12px;
  line-height: 1.5;
  word-break: break-word;
}

.magic-test-output {
  width: min(50%, 100%);
  min-width: 0;
  margin-top: 4px;
  font-size: 12px;
  line-height: 1.5;
  word-break: break-word;
  color: #16a34a;
}

.user-field-success {
  border-color: #4ade80;
}

.enable-switch {
  position: relative;
  display: inline-flex;
  width: 38px;
  height: 20px;
  flex: 0 0 auto;
  align-items: center;
}

.enable-switch input {
  opacity: 0;
  width: 0;
  height: 0;
}

.enable-slider {
  position: absolute;
  inset: 0;
  border-radius: 9999px;
  background: #d1d5db;
  cursor: pointer;
  transition: background-color 0.18s ease;
}

.enable-slider::before {
  content: '';
  position: absolute;
  width: 16px;
  height: 16px;
  left: 2px;
  top: 2px;
  border-radius: 9999px;
  background: #ffffff;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.18);
  transition: transform 0.18s ease;
}

.enable-switch input:checked + .enable-slider {
  background: #22c55e;
}

.enable-switch input:checked + .enable-slider::before {
  transform: translateX(18px);
}

.user-panel__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 0.75rem;
}

.user-add-form {
  margin-bottom: 1rem;
}

.user-form-grid {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 14px 18px;
}

.user-form-grid__label {
  white-space: nowrap;
}

.user-form-actions {
  display: flex;
  justify-content: flex-start;
  margin-top: 0.75rem;
}

.btn-add {
  padding: 8px 12px;
  border-radius: 10px;
  appearance: none;
  -webkit-appearance: none;
  border: 1px solid rgba(34, 197, 94, 0.4);
  background: rgba(34, 197, 94, 0.08);
  color: #15803d;
  font-size: 14px;
  font-weight: 600;
  transition: all 0.2s ease;
  cursor: pointer;
}

.btn-add:hover {
  background: rgba(22, 163, 74, 0.16);
}

.btn-add.active {
  background: #22c55e;
  color: #fff;
  border-color: transparent;
}

.btn-add.active:hover {
  background: #16a34a;
}

.user-table-wrap {
  overflow-x: auto;
}

.user-table-shell {
  display: inline-block;
  width: max-content;
  min-width: 100%;
  vertical-align: top;
}

.user-table {
  width: max-content;
  min-width: 100%;
  table-layout: auto;
  font-size: 0.875rem;
  line-height: 1.25rem;
  text-align: left;
  color: #374151;
  border-collapse: collapse;
}

.user-table th,
.user-table td {
  padding: 0.5rem 0.75rem;
  white-space: nowrap;
}

.user-table tbody tr + tr {
  border-top: 1px solid #f3f4f6;
}

.table-head {
  background: #f8fafc;
}

.table-head th {
  font-weight: 600;
  color: #4b5563;
}

.user-table__name {
  font-weight: 600;
}

.user-table__empty {
  color: #6b7280;
}

.tag-yellow,
.tag-gray,
.tag-green,
.tag-red {
  display: inline-flex;
  align-items: center;
  padding: 4px 8px;
  border-radius: 9999px;
  font-size: 12px;
  line-height: 1;
}

.availability-tag {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.availability-tag svg {
  width: 12px;
  height: 12px;
  flex: 0 0 auto;
}

.tag-yellow {
  background: #fef3c7;
  color: #ca8a04;
}

.tag-gray {
  background: #f3f4f6;
  color: #4b5563;
}

.tag-green {
  background: #dcfce7;
  color: #15803d;
}

.tag-red {
  background: #fee2e2;
  color: #b91c1c;
}

.action-group {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex-wrap: nowrap;
}

.action-btn {
  appearance: none;
  -webkit-appearance: none;
  border: 0;
  border-radius: 9999px;
  padding: 4px 8px;
  font-size: 12px;
  font-weight: 600;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 52px;
  white-space: nowrap;
  transition: all 0.15s ease;
  cursor: pointer;
}

.action-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.action-btn.blue {
  background: #dbeafe;
  color: #1d4ed8;
}

.action-btn.rose {
  background: #ffe4e6;
  color: #e11d48;
}

.action-btn.red {
  background: #fee2e2;
  color: #b91c1c;
}

.action-btn.green {
  background: #22c55e;
  color: #fff;
}

.action-btn.gray {
  background: #e5e7eb;
  color: #374151;
}

.go-proxy-pan-switches {
  display: inline-flex;
  align-items: center;
  gap: 16px;
  flex-wrap: nowrap;
}

.go-proxy-pan-switch {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.user-edit-row > td {
  padding-top: 0.75rem;
  padding-bottom: 0.75rem;
}

.user-edit-box {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.user-edit-actions {
  margin-top: 0.75rem;
}

.mf-toast-container {
  position: fixed;
  top: 20px;
  right: 20px;
  z-index: 1400;
  display: flex;
  flex-direction: column;
  gap: 12px;
  pointer-events: none;
}

.mf-toast {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 12px;
  min-width: 280px;
  max-width: 420px;
  padding: 14px 16px;
  border-radius: 14px;
  border: 1px solid rgba(229, 231, 235, 0.9);
  background: rgba(255, 255, 255, 0.96);
  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.16);
  backdrop-filter: blur(18px);
  -webkit-backdrop-filter: blur(18px);
  pointer-events: auto;
}

.mf-toast-bar {
  position: absolute;
  left: 0;
  top: 10px;
  bottom: 10px;
  width: 4px;
  border-radius: 9999px;
  background: #94a3b8;
}

.mf-toast--success .mf-toast-bar { background: #22c55e; }
.mf-toast--error .mf-toast-bar { background: #ef4444; }
.mf-toast--info .mf-toast-bar { background: #60a5fa; }

.mf-toast-icon {
  flex: 0 0 auto;
  width: 20px;
  height: 20px;
  color: #64748b;
  margin-top: 1px;
}

.mf-toast--success .mf-toast-icon { color: #16a34a; }
.mf-toast--error .mf-toast-icon { color: #dc2626; }
.mf-toast--info .mf-toast-icon { color: #2563eb; }

.mf-toast-icon :deep(svg),
.mf-toast-icon svg {
  display: block;
  width: 20px;
  height: 20px;
}

.mf-toast-body {
  min-width: 0;
  flex: 1 1 auto;
}

.mf-toast-text {
  font-size: 14px;
  line-height: 1.5;
  color: #0f172a;
  word-break: break-word;
}

.mf-toast-close {
  flex: 0 0 auto;
  appearance: none;
  -webkit-appearance: none;
  border: 0;
  background: transparent;
  color: #94a3b8;
  padding: 0;
  width: 18px;
  height: 18px;
  cursor: pointer;
}

.mf-toast-close:hover {
  color: #475569;
}

.mf-toast-close svg {
  display: block;
  width: 16px;
  height: 16px;
}

.thirdparty-selectbox {
  min-width: 112px;
  position: relative;
  overflow: visible;
  z-index: 60;
}

.thirdparty-selectbox .ui-selectbox__trigger {
  min-width: 112px;
  padding-right: 34px;
}

.thirdparty-selectbox .ui-selectbox__trigger::after {
  opacity: 1;
}

.thirdparty-selectbox .ui-selectbox__trigger:disabled {
  background: #f8fafc;
  color: #64748b;
  cursor: not-allowed;
}

.thirdparty-selectbox .ui-selectbox__menu {
  left: 0;
  right: auto;
  min-width: 100%;
  width: max-content;
  z-index: 40000;
}

.thirdparty-config-table,
.thirdparty-config-table table,
.thirdparty-config-table tbody,
.thirdparty-config-table tr,
.thirdparty-config-table td {
  overflow: visible;
}

.thirdparty-config-grid td {
  position: relative;
  z-index: 1;
}

.thirdparty-config-grid td:has(.thirdparty-selectbox .ui-selectbox__menu) {
  z-index: 80;
}

@media (max-width: 768px) {
  .mf-toast-container {
    left: 12px;
    right: 12px;
    top: 12px;
  }

  .mf-toast {
    min-width: 0;
    width: 100%;
    max-width: none;
  }
}
</style>
