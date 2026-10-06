// Testes unitários de lógica pura (sem React Native): ts-jest em ambiente node.
// Componentes e telas não são cobertos aqui; para eles seria necessário jest-expo.
/** @type {import('jest').Config} */
module.exports = {
  testEnvironment: 'node',
  roots: ['<rootDir>/src'],
  testMatch: ['**/__tests__/**/*.test.ts', '**/*.test.ts'],
  moduleNameMapper: {
    '^@/(.*)$': '<rootDir>/src/$1',
  },
  transform: {
    '^.+\.ts$': [
      'ts-jest',
      {
        // O tsconfig do app usa moduleResolution "bundler" (Metro); o Jest roda em CommonJS.
        tsconfig: {
          module: 'commonjs',
          moduleResolution: 'node10',
          target: 'es2020',
          esModuleInterop: true,
          resolveJsonModule: true,
          strict: true,
          skipLibCheck: true,
          isolatedModules: true,
          baseUrl: '.',
          paths: { '@/*': ['./src/*'] },
        },
      },
    ],
  },
  clearMocks: true,
};
