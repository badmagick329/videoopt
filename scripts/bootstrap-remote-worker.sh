#!/usr/bin/env bash
set -Eeuo pipefail

readonly PREFIX="/opt/video-optimiser"
readonly WORK_ROOT="/var/tmp/video-optimiser"
readonly WORKER_USER="videoopt"
readonly WORKER_HOME="/var/lib/videoopt"
# Each version has its pin on the next line: the commit its tag named, or the SHA-256 of the file it downloads. A moved
# tag or a replaced upstream file then fails the bootstrap instead of building into the worker. Bump the two together;
# ToolVersionPinTests fails when a version has no pin beside it, or when a pair isn't the one it lists.
readonly VMAF_VERSION="3.0.0"
readonly VMAF_COMMIT="17a67b238ce0539bdeafdc95961abac64fa16ea8"
readonly SVT_AV1_VERSION="3.1.0"
readonly SVT_AV1_COMMIT="cc569c1eb16832b7f1a649cac0b3bd1a8e4a814d"
readonly FFMPEG_VERSION="8.0"
readonly FFMPEG_SHA256="b2751fccb6cc4c77708113cd78b561059b6fa904b24162fa0be2d60273d27b8e"
# Same version as scripts/install-media-tools.ps1, which CI tests; ToolVersionPinTests fails when they differ.
readonly AB_AV1_VERSION="0.11.7"
readonly AB_AV1_SHA256="40c035df608162fcb1c991d1474bc9bc8817e0354ac7aef5be5314d3efd64069"
readonly AUTHORIZED_KEYS_SOURCE="${VIDEOOPT_AUTHORIZED_KEYS_FILE:-/root/.ssh/authorized_keys}"
readonly PROGRESS_FILE="${PREFIX}/.bootstrap-progress"
# Without it a failed script looks like a slow one, and the controller waits out the whole bootstrapTimeout (VOPT-140).
readonly FAILED_FILE="${PREFIX}/.bootstrap-failed"
current_stage="Starting"

write_progress() {
    local percent="$1"
    local stage="$2"
    local temporary="${PROGRESS_FILE}.tmp"

    install -d -m 0755 "${PREFIX}"
    {
        printf 'percent=%s\n' "${percent}"
        printf 'stage=%s\n' "${stage}"
    } > "${temporary}"
    mv -f -- "${temporary}" "${PROGRESS_FILE}"
    current_stage="${stage}"
}

# Each argument is one key=value line; a newline in a value would split it, so it becomes a space. Best effort: the
# script is already failing, so a marker that can't be written must not hide the original error.
write_failure() {
    local temporary="${FAILED_FILE}.tmp"
    local line

    install -d -m 0755 "${PREFIX}" 2>/dev/null || return 0
    if {
        printf 'stage=%s\n' "${current_stage}"
        for line in "$@"; do
            printf '%s\n' "${line//$'\n'/ }"
        done
    } > "${temporary}" 2>/dev/null; then
        mv -f -- "${temporary}" "${FAILED_FILE}" 2>/dev/null || true
    fi
}

on_error() {
    trap - ERR
    write_failure "status=$1" "line=$2" "command=$3"
}

die() {
    printf 'bootstrap error: %s\n' "$*" >&2
    trap - ERR
    write_failure "error=$*"
    exit 1
}

# set -E carries the trap into functions and subshells. die exits without tripping it, so it writes its own marker.
trap 'on_error "$?" "${LINENO}" "${BASH_COMMAND}"' ERR

# Bash fires no ERR trap for a command in a || list, so each check ends in die, which writes the failure marker itself.
verify_sha256() {
    local file="$1"
    local expected="$2"

    printf '%s  %s\n' "${expected}" "${file}" | sha256sum --check --status \
        || die "${file##*/} doesn't match the SHA-256 pinned in the bootstrap script"
}

# Fetches the pinned commit itself rather than a tag, which upstream can move to other code.
fetch_commit() {
    local url="$1"
    local commit="$2"
    local directory="$3"

    git init --quiet "${directory}"
    git -C "${directory}" fetch --quiet --depth 1 "${url}" "${commit}"
    git -C "${directory}" -c advice.detachedHead=false checkout --quiet FETCH_HEAD
    [[ "$(git -C "${directory}" rev-parse HEAD)" == "${commit}" ]] \
        || die "the commit fetched from ${url} isn't the one pinned in the bootstrap script"
}

if [[ "${EUID}" -ne 0 ]]; then
    die "run this script as root"
fi

install -d -m 0755 "${PREFIX}"
# A marker left by an earlier failed attempt would end this one's wait at once.
rm -f -- "${FAILED_FILE}"
write_progress 0 "Starting"

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

write_progress 2 "Validating worker image"
export DEBIAN_FRONTEND=noninteractive
write_progress 5 "Installing system dependencies"
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

install -d -m 0755 "${PREFIX}/bin" "${PREFIX}/lib" "${PREFIX}/include" "${PREFIX}/share"
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
    write_progress 15 "Building libvmaf"
    fetch_commit https://github.com/Netflix/vmaf.git "${VMAF_COMMIT}" "${build_dir}/vmaf"
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
    write_progress 35 "Building SVT-AV1"
    fetch_commit https://gitlab.com/AOMediaCodec/SVT-AV1.git "${SVT_AV1_COMMIT}" "${build_dir}/svt-av1"
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
    write_progress 60 "Building FFmpeg"
    curl --fail --location --retry 5 --proto '=https' --tlsv1.2 \
        "https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz" \
        --output "${build_dir}/ffmpeg.tar.xz"
    verify_sha256 "${build_dir}/ffmpeg.tar.xz" "${FFMPEG_SHA256}"
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
    write_progress 90 "Installing ab-av1"
    curl --fail --location --retry 5 --proto '=https' --tlsv1.2 \
        "https://github.com/alexheretic/ab-av1/releases/download/v${AB_AV1_VERSION}/ab-av1-v${AB_AV1_VERSION}-x86_64-unknown-linux-musl.tar.zst" \
        --output "${build_dir}/ab-av1.tar.zst"
    verify_sha256 "${build_dir}/ab-av1.tar.zst" "${AB_AV1_SHA256}"
    tar --zstd -xf "${build_dir}/ab-av1.tar.zst" -C "${build_dir}"
    install -m 0755 "${build_dir}/ab-av1" "${PREFIX}/bin/ab-av1"
    touch "${PREFIX}/.versions/ab-av1-${AB_AV1_VERSION}"
fi

ln -sfn "${PREFIX}/bin/ab-av1" /usr/local/bin/ab-av1
ln -sfn "${PREFIX}/bin/ffmpeg" /usr/local/bin/ffmpeg
ln -sfn "${PREFIX}/bin/ffprobe" /usr/local/bin/ffprobe

write_progress 94 "Configuring worker account and SSH"
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

write_progress 98 "Verifying installed codecs"
"${PREFIX}/bin/ffmpeg" -hide_banner -encoders 2>/dev/null | grep 'libsvtav1' >/dev/null \
    || die "FFmpeg does not expose the libsvtav1 encoder"
"${PREFIX}/bin/ffmpeg" -hide_banner -decoders 2>/dev/null \
    | grep -E '[[:space:]]libdav1d([[:space:]]|$)' >/dev/null \
    || die "FFmpeg does not expose the libdav1d AV1 decoder"
"${PREFIX}/bin/ffmpeg" -hide_banner -filters 2>/dev/null | grep 'libvmaf' >/dev/null \
    || die "FFmpeg does not expose the libvmaf filter"
"${PREFIX}/bin/ffmpeg" -hide_banner -pix_fmts 2>/dev/null | grep 'yuv420p10le' >/dev/null \
    || die "FFmpeg does not expose the yuv420p10le pixel format"

touch "${PREFIX}/.bootstrap-complete"
write_progress 100 "Ready"

printf '\nRemote worker ready.\n'
"${PREFIX}/bin/ab-av1" --version
"${PREFIX}/bin/ffmpeg" -version | sed -n '1p'
printf 'SVT-AV1 %s; libvmaf %s\n' \
    "$(pkg-config --modversion SvtAv1Enc)" \
    "$(pkg-config --modversion libvmaf)"
printf 'SSH user: %s\nWorking directory: %s\n' "${WORKER_USER}" "${WORK_ROOT}"
printf 'Worker bootstrap complete. The controller may now begin processing.\n'
