#!/usr/bin/env bash
set -Eeuo pipefail

readonly PREFIX="/opt/video-optimiser"
readonly WORK_ROOT="/var/tmp/video-optimiser"
readonly WORKER_USER="videoopt"
readonly WORKER_HOME="/var/lib/videoopt"
readonly VMAF_VERSION="3.0.0"
readonly SVT_AV1_VERSION="3.1.0"
readonly FFMPEG_VERSION="8.0"
readonly AB_AV1_VERSION="0.10.4"
readonly AUTHORIZED_KEYS_SOURCE="${VIDEOOPT_AUTHORIZED_KEYS_FILE:-/root/.ssh/authorized_keys}"

die() {
    printf 'bootstrap error: %s\n' "$*" >&2
    exit 1
}

if [[ "${EUID}" -ne 0 ]]; then
    die "run this script as root"
fi

if [[ "$(uname -m)" != "x86_64" ]]; then
    die "Ubuntu 24.04 x86-64 is required"
fi

# shellcheck disable=SC1091
source /etc/os-release
if [[ "${ID:-}" != "ubuntu" || "${VERSION_ID:-}" != "24.04" ]]; then
    die "Ubuntu 24.04 x86-64 is required"
fi

if [[ ! -s "${AUTHORIZED_KEYS_SOURCE}" && ! -s "${WORKER_HOME}/.ssh/authorized_keys" ]]; then
    die "no SSH keys found; create the server with an SSH key or set VIDEOOPT_AUTHORIZED_KEYS_FILE"
fi

if [[ -L "${WORK_ROOT}" || ( -e "${WORK_ROOT}" && ! -d "${WORK_ROOT}" ) ]]; then
    die "${WORK_ROOT} must be a real directory, not a file or symbolic link"
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install --yes --no-install-recommends \
    autoconf \
    automake \
    build-essential \
    ca-certificates \
    cmake \
    curl \
    git \
    libdav1d-dev \
    libnuma-dev \
    libopus-dev \
    libtool \
    meson \
    nasm \
    ninja-build \
    pkg-config \
    python3-setuptools \
    openssh-server \
    time \
    xxd \
    xz-utils \
    yasm \
    zstd

install -d -m 0755 "${PREFIX}" "${PREFIX}/bin" "${PREFIX}/lib" "${PREFIX}/include" "${PREFIX}/share"
install -d -m 0755 "${PREFIX}/.versions"

build_dir="$(mktemp -d /var/tmp/video-optimiser-bootstrap.XXXXXX)"
cleanup() {
    if [[ "${build_dir}" == /var/tmp/video-optimiser-bootstrap.* && -d "${build_dir}" ]]; then
        rm -rf -- "${build_dir}"
    fi
}
trap cleanup EXIT

export PKG_CONFIG_PATH="${PREFIX}/lib/pkgconfig"
export PATH="${PREFIX}/bin:${PATH}"

if [[ ! -f "${PREFIX}/.versions/libvmaf-${VMAF_VERSION}" ]] || \
   [[ "$(pkg-config --modversion libvmaf 2>/dev/null || true)" != "${VMAF_VERSION}" ]]; then
    git clone --depth 1 --branch "v${VMAF_VERSION}" --single-branch \
        https://github.com/Netflix/vmaf.git "${build_dir}/vmaf"
    meson setup "${build_dir}/vmaf/libvmaf/build" "${build_dir}/vmaf/libvmaf" \
        --buildtype=release \
        --prefix="${PREFIX}" \
        --libdir=lib \
        -Denable_tests=false \
        -Denable_docs=false
    ninja -C "${build_dir}/vmaf/libvmaf/build" install
    touch "${PREFIX}/.versions/libvmaf-${VMAF_VERSION}"
fi

if [[ ! -f "${PREFIX}/.versions/svt-av1-${SVT_AV1_VERSION}" ]] || \
   [[ "$(pkg-config --modversion SvtAv1Enc 2>/dev/null || true)" != "${SVT_AV1_VERSION}" ]]; then
    git clone --depth 1 --branch "v${SVT_AV1_VERSION}" --single-branch \
        https://gitlab.com/AOMediaCodec/SVT-AV1.git "${build_dir}/svt-av1"
    cmake -S "${build_dir}/svt-av1" -B "${build_dir}/svt-av1-build" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
        -DCMAKE_INSTALL_LIBDIR=lib \
        -DBUILD_APPS=OFF \
        -DBUILD_SHARED_LIBS=ON
    cmake --build "${build_dir}/svt-av1-build" --parallel "$(nproc)"
    cmake --install "${build_dir}/svt-av1-build"
    touch "${PREFIX}/.versions/svt-av1-${SVT_AV1_VERSION}"
fi

printf '%s\n' "${PREFIX}/lib" > /etc/ld.so.conf.d/video-optimiser.conf
ldconfig

ffmpeg_version="$("${PREFIX}/bin/ffmpeg" -version 2>/dev/null | sed -n '1p' || true)"
ffmpeg_has_libdav1d=false
if "${PREFIX}/bin/ffmpeg" -hide_banner -decoders 2>/dev/null \
    | grep -E '[[:space:]]libdav1d([[:space:]]|$)' >/dev/null; then
    ffmpeg_has_libdav1d=true
fi
if [[ ! -f "${PREFIX}/.versions/ffmpeg-${FFMPEG_VERSION}" ]] || \
   [[ ! "${ffmpeg_version}" =~ ^ffmpeg\ version\ ${FFMPEG_VERSION}([[:space:]]|$) ]] || \
   [[ "${ffmpeg_has_libdav1d}" != true ]]; then
    curl --fail --location --retry 5 --proto '=https' --tlsv1.2 \
        "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz" \
        --output "${build_dir}/ffmpeg.tar.xz"
    tar -xJf "${build_dir}/ffmpeg.tar.xz" -C "${build_dir}"
    pushd "${build_dir}/ffmpeg-${FFMPEG_VERSION}" >/dev/null
    PKG_CONFIG_PATH="${PKG_CONFIG_PATH}" ./configure \
        --prefix="${PREFIX}" \
        --disable-debug \
        --disable-doc \
        --enable-libdav1d \
        --enable-libopus \
        --enable-libsvtav1 \
        --enable-libvmaf \
        --extra-cflags="-I${PREFIX}/include" \
        --extra-ldflags="-L${PREFIX}/lib -Wl,-rpath,${PREFIX}/lib"
    make --jobs "$(nproc)"
    make install
    popd >/dev/null
    touch "${PREFIX}/.versions/ffmpeg-${FFMPEG_VERSION}"
fi

if [[ ! -f "${PREFIX}/.versions/ab-av1-${AB_AV1_VERSION}" ]] || \
   [[ "$("${PREFIX}/bin/ab-av1" --version 2>/dev/null || true)" != "ab-av1 ${AB_AV1_VERSION}" ]]; then
    curl --fail --location --retry 5 --proto '=https' --tlsv1.2 \
        "https://github.com/alexheretic/ab-av1/releases/download/v${AB_AV1_VERSION}/ab-av1-v${AB_AV1_VERSION}-x86_64-unknown-linux-musl.tar.zst" \
        --output "${build_dir}/ab-av1.tar.zst"
    tar --zstd -xf "${build_dir}/ab-av1.tar.zst" -C "${build_dir}"
    install -m 0755 "${build_dir}/ab-av1" "${PREFIX}/bin/ab-av1"
    touch "${PREFIX}/.versions/ab-av1-${AB_AV1_VERSION}"
fi

ln -sfn "${PREFIX}/bin/ab-av1" /usr/local/bin/ab-av1
ln -sfn "${PREFIX}/bin/ffmpeg" /usr/local/bin/ffmpeg
ln -sfn "${PREFIX}/bin/ffprobe" /usr/local/bin/ffprobe

if ! id "${WORKER_USER}" >/dev/null 2>&1; then
    useradd --create-home --home-dir "${WORKER_HOME}" --shell /bin/bash --user-group "${WORKER_USER}"
elif [[ "$(getent passwd "${WORKER_USER}" | cut -d: -f6)" != "${WORKER_HOME}" ]] || \
     [[ "$(id -gn "${WORKER_USER}")" != "${WORKER_USER}" ]]; then
    die "existing ${WORKER_USER} account does not match the dedicated worker account"
fi
usermod --lock "${WORKER_USER}"

install -d -m 0755 -o root -g root "${WORKER_HOME}"
install -d -m 0750 -o root -g "${WORKER_USER}" "${WORKER_HOME}/.ssh"
if [[ -s "${AUTHORIZED_KEYS_SOURCE}" && "${AUTHORIZED_KEYS_SOURCE}" != "${WORKER_HOME}/.ssh/authorized_keys" ]]; then
    install -m 0640 -o root -g "${WORKER_USER}" "${AUTHORIZED_KEYS_SOURCE}" "${WORKER_HOME}/.ssh/authorized_keys"
fi
chown root:"${WORKER_USER}" "${WORKER_HOME}/.ssh/authorized_keys"
chmod 0640 "${WORKER_HOME}/.ssh/authorized_keys"
runuser --user "${WORKER_USER}" -- test -r "${WORKER_HOME}/.ssh/authorized_keys" \
    || die "${WORKER_USER} cannot read its authorized_keys file"
if runuser --user "${WORKER_USER}" -- test -w "${WORKER_HOME}/.ssh/authorized_keys"; then
    die "${WORKER_USER} must not be able to modify its authorized_keys file"
fi
install -d -m 0750 -o "${WORKER_USER}" -g "${WORKER_USER}" "${WORK_ROOT}"

cat > /etc/ssh/sshd_config.d/video-optimiser.conf <<'EOF'
Match User videoopt
    AuthenticationMethods publickey
    PubkeyAuthentication yes
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    AllowAgentForwarding no
    AllowTcpForwarding no
    PermitTunnel no
    X11Forwarding no
EOF

sshd -t
effective_sshd_config="$(sshd -T -C user="${WORKER_USER}",host=localhost,addr=127.0.0.1)"
grep -Fx 'authenticationmethods publickey' <<<"${effective_sshd_config}" >/dev/null \
    || die "public-key-only SSH authentication is not effective for ${WORKER_USER}"
grep -Fx 'pubkeyauthentication yes' <<<"${effective_sshd_config}" >/dev/null \
    || die "public-key SSH authentication is not enabled for ${WORKER_USER}"
grep -Fx 'passwordauthentication no' <<<"${effective_sshd_config}" >/dev/null \
    || die "password SSH authentication is still enabled for ${WORKER_USER}"
systemctl reload ssh

"${PREFIX}/bin/ffmpeg" -hide_banner -encoders 2>/dev/null | grep 'libsvtav1' >/dev/null \
    || die "FFmpeg does not expose the libsvtav1 encoder"
"${PREFIX}/bin/ffmpeg" -hide_banner -decoders 2>/dev/null \
    | grep -E '[[:space:]]libdav1d([[:space:]]|$)' >/dev/null \
    || die "FFmpeg does not expose the libdav1d AV1 decoder"
"${PREFIX}/bin/ffmpeg" -hide_banner -filters 2>/dev/null | grep 'libvmaf' >/dev/null \
    || die "FFmpeg does not expose the libvmaf filter"
"${PREFIX}/bin/ffmpeg" -hide_banner -pix_fmts 2>/dev/null | grep 'yuv420p10le' >/dev/null \
    || die "FFmpeg does not expose the yuv420p10le pixel format"

printf '\nRemote worker ready.\n'
"${PREFIX}/bin/ab-av1" --version
"${PREFIX}/bin/ffmpeg" -version | sed -n '1p'
printf 'SVT-AV1 %s; libvmaf %s\n' \
    "$(pkg-config --modversion SvtAv1Enc)" \
    "$(pkg-config --modversion libvmaf)"
printf 'SSH user: %s\nWorking directory: %s\n' "${WORKER_USER}" "${WORK_ROOT}"
printf 'Server lifecycle is manual: DELETE the cloud server after verified downloads; powering it off still bills.\n'
