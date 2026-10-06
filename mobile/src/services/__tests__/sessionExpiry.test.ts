import { handleSessionExpired, type SessionExpiryDeps } from '../sessionExpiry';

function makeDeps(overrides: Partial<SessionExpiryDeps> = {}) {
  const order: string[] = [];
  const deps = {
    getRefreshToken: jest.fn(() => 'refresh-1'),
    getDevicePushToken: jest.fn(async () => 'fcm-device'),
    clearUserData: jest.fn(async () => {
      order.push('clear');
    }),
    notifySignedOut: jest.fn(() => {
      order.push('notify');
    }),
    revokeOnServer: jest.fn(async () => {
      order.push('revoke');
    }),
    ...overrides,
  } satisfies SessionExpiryDeps;
  return { deps, order };
}

const settle = () => new Promise((resolve) => setImmediate(resolve));

describe('sessão expirada', () => {
  it('limpa o aparelho, avisa o usuário e desregistra o token push com o refresh token anotado antes da limpeza', async () => {
    const { deps, order } = makeDeps();

    await handleSessionExpired(deps);
    await settle();

    expect(order).toEqual(['clear', 'notify', 'revoke']);
    expect(deps.revokeOnServer).toHaveBeenCalledWith('refresh-1', 'fcm-device');
  });

  it('sem token push no aparelho não chama o servidor', async () => {
    const { deps } = makeDeps({ getDevicePushToken: jest.fn(async () => null) });

    await handleSessionExpired(deps);
    await settle();

    expect(deps.clearUserData).toHaveBeenCalled();
    expect(deps.revokeOnServer).not.toHaveBeenCalled();
  });

  it('sem refresh token guardado só limpa', async () => {
    const { deps } = makeDeps({ getRefreshToken: jest.fn(() => null) });

    await handleSessionExpired(deps);
    await settle();

    expect(deps.revokeOnServer).not.toHaveBeenCalled();
  });

  it('o servidor fora do ar ou a busca do token falhando não impedem a saída', async () => {
    const failing = makeDeps({ revokeOnServer: jest.fn(async () => { throw new Error('offline'); }) });
    await expect(handleSessionExpired(failing.deps)).resolves.toBeUndefined();
    await settle();
    expect(failing.deps.notifySignedOut).toHaveBeenCalled();

    const noToken = makeDeps({ getDevicePushToken: jest.fn(async () => { throw new Error('sem play services'); }) });
    await expect(handleSessionExpired(noToken.deps)).resolves.toBeUndefined();
    expect(noToken.deps.notifySignedOut).toHaveBeenCalled();
  });
});
