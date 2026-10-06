// Blocos de texto corrido com título de seção (telas de privacidade e consentimento).
import React from 'react';
import { View, Text, StyleSheet } from 'react-native';
import { colors } from '@/theme';
import type { TextSection } from '@/modules/privacy/privacyContent';

export function TextSections({ sections }: { sections: readonly TextSection[] }) {
  return (
    <View>
      {sections.map((section) => (
        <View key={section.title} style={styles.section}>
          <Text style={styles.heading} accessibilityRole="header">{section.title}</Text>
          {section.paragraphs.map((paragraph) => (
            <Text key={paragraph} style={styles.paragraph}>{paragraph}</Text>
          ))}
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  section: { marginBottom: 20 },
  heading: { fontSize: 16, fontWeight: '700', color: colors.text, marginBottom: 8 },
  paragraph: { fontSize: 14, lineHeight: 21, color: colors.textSubtle, marginBottom: 8 },
});
