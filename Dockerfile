FROM "steamcmd/steamcmd:ubuntu-24"

RUN echo "バージョン(キャッシュ回避用に変更): 1.1.2.$(date +%s)"
RUN ln -sf /usr/share/zoneinfo/Asia/Tokyo /etc/localtime

RUN apt update;apt install wget curl net-tools tini tzdata jq unzip -y;
RUN curl -fsSL https://tailscale.com/install.sh | sh
RUN rm -rf /var/lib/apt/lists/*
RUN wget https://github.com/gorcon/rcon-cli/releases/download/v0.10.3/rcon-0.10.3-amd64_linux.tar.gz -O rcon.tar.gz && tar -zxf rcon.tar.gz -C /tmp/ --wildcards rcon*/rcon && cp /tmp/rcon*/rcon /usr/local/bin/ && rm rcon.tar.gz && rm -r /tmp/rcon*

ARG ADMIN_RADAR_PLUGIN_URL=https://umod.org/plugins/AdminRadar.cs
ARG INVENTORY_VIEWER_PLUGIN_URL=https://umod.org/plugins/InventoryViewer.cs
ARG PLAYER_ADMINISTRATION_PLUGIN_URL=https://umod.org/plugins/PlayerAdministration.cs
ARG ADMIN_LOGGER_PLUGIN_URL=https://umod.org/plugins/AdminLogger.cs
ARG VANISH_PLUGIN_URL=https://umod.org/plugins/Vanish.cs

ENV ENV_ENABLE_UMOD=true \
    ENV_ENABLE_ADMIN_RADAR=true \
    ENV_ENABLE_INVENTORY_VIEWER=true \
    ENV_ENABLE_PLAYER_ADMINISTRATION=true \
    ENV_ENABLE_ADMIN_LOGGER=true \
    ENV_ENABLE_VANISH=true \
    ENV_UMOD_MODDED=false \
    ENV_UMOD_DOWNLOAD_URL=https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip

RUN mkdir -p /root/rustserver
RUN mkdir -p /opt/rustserver/plugins \
    && curl -fsSL "${ADMIN_RADAR_PLUGIN_URL}" -o /opt/rustserver/plugins/AdminRadar.cs \
    && curl -fsSL "${INVENTORY_VIEWER_PLUGIN_URL}" -o /opt/rustserver/plugins/InventoryViewer.cs \
    && curl -fsSL "${PLAYER_ADMINISTRATION_PLUGIN_URL}" -o /opt/rustserver/plugins/PlayerAdministration.cs \
    && curl -fsSL "${ADMIN_LOGGER_PLUGIN_URL}" -o /opt/rustserver/plugins/AdminLogger.cs \
    && curl -fsSL "${VANISH_PLUGIN_URL}" -o /opt/rustserver/plugins/Vanish.cs

COPY launch.sh /root/rustserver/launch.sh
RUN chmod +x /root/rustserver/launch.sh

WORKDIR /root/rustserver
ENTRYPOINT tini -- ./launch.sh
