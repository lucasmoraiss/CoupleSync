// O recurso de IA (aba "Chat IA" e categorização de extratos pelo Google Gemini) existe nesta versão do app?
// Decidido na geração do pacote: só com EXPO_PUBLIC_AI_CHAT_ENABLED=true. Um único lugar, para que a aba, o
// texto de privacidade e o envio de extratos concordem. (O acesso direto a process.env.EXPO_PUBLIC_* é o que
// o Expo substitui pelo valor na geração; não troque por acesso dinâmico.)
export const AI_FEATURE_ENABLED = process.env.EXPO_PUBLIC_AI_CHAT_ENABLED === 'true';
