// AC-124, AC-126, AC-127: Expo Router entry — delegates to OcrReviewScreen
import { useLocalSearchParams } from 'expo-router';
import OcrReviewScreen from '@/modules/ocr/screens/OcrReviewScreen';

export default function OcrReviewPage() {
  const { uploadId } = useLocalSearchParams<{ uploadId: string }>();
  return <OcrReviewScreen uploadId={uploadId ?? ''} />;
}
