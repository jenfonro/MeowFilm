import { computed, ref } from 'vue';
import { saveManualRecognizeSelection } from './manualRecognize';
import { normalizeString } from './normalize';

export const useManualRecognizeDialog = () => {
  const dialogOpen = ref(false);
  const dialogKeyword = ref('');
  const dialogSaving = ref(false);
  const dialogContext = ref({ siteKey: '', spiderApi: '', siteDetail: '' });

  const canSubmitDialog = computed(() => (
    !!normalizeString(dialogContext.value && dialogContext.value.siteKey)
    && !!normalizeString(dialogContext.value && dialogContext.value.spiderApi)
    && !!normalizeString(dialogContext.value && dialogContext.value.siteDetail)
  ));

  const openManualRecognizeDialog = (payload) => {
    const current = payload && typeof payload === 'object' ? payload : {};
    dialogContext.value = {
      siteKey: normalizeString(current.siteKey),
      spiderApi: normalizeString(current.spiderApi),
      siteDetail: normalizeString(current.siteDetail),
    };
    dialogKeyword.value = normalizeString(current.keyword);
    dialogOpen.value = true;
  };

  const submitManualRecognizeDialog = async (selected) => {
    if (dialogSaving.value || !canSubmitDialog.value) return;
    dialogSaving.value = true;
    try {
      await saveManualRecognizeSelection(selected, dialogContext.value);
      dialogOpen.value = false;
    } catch (error) {
      console.error('[manual-recognize] add failed', error);
    } finally {
      dialogSaving.value = false;
    }
  };

  return {
    dialogOpen,
    dialogKeyword,
    dialogSaving,
    canSubmitDialog,
    openManualRecognizeDialog,
    submitManualRecognizeDialog,
  };
};
