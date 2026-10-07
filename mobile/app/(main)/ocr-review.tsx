// AC-124, AC-126, AC-127: Expo Router entry — delegates to OcrReviewScreen
import { useLocalSearchParams } from 'expo-router';
import OcrReviewScreen from '@/modules/ocr/screens/OcrReviewScreen';
import { resetOnFocus } from '@/navigation/resetOnFocus';

function OcrReviewPage() {
  const { uploadId } = useLocalSearchParams<{ uploadId: string }>();
  // This route is a hidden tab and stays mounted between imports. Keying the screen by
  // uploadId remounts it for every new upload, so rows, selection, edits and the success
  // banner of a previous import can never be shown (or confirmed) under the new uploadId.
  return <OcrReviewScreen key={uploadId ?? ''} uploadId={uploadId ?? ''} />;
}

// Reopening the SAME import (after "Confirmar e continuar depois") must not show the rows, ticks and success
// banner of the previous visit either: every visit remounts the screen, which drops its state and (the results
// query has gcTime 0) loads the lines as they are on the server now.
export default resetOnFocus(OcrReviewPage);
