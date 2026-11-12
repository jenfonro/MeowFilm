export function nativeSourceInputs(policy) {
  return Object.values(policy.sources ?? {}).flatMap(source => [
    source.archivePath,
    source.license?.path,
    ...(source.unicodeLicense ? [source.unicodeLicense.path] : []),
  ].filter(Boolean));
}

export function lockedNodeHeaders(policy) {
  const version = policy.build?.node;
  return {
    version,
    url: `https://nodejs.org/dist/v${version}/node-v${version}-headers.tar.gz`,
    checksumsUrl: `https://nodejs.org/dist/v${version}/SHASUMS256.txt`,
    sha256: policy.nodeHeaders?.sha256,
    archivePath: policy.nodeHeaders?.archivePath,
  };
}

export function iosTargetPolicy(policy, sdk, arch) {
  return policy.ios?.targets?.find(target => target.sdk === sdk && target.arch === arch) ?? null;
}
