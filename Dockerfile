FROM "steamcmd/steamcmd:ubuntu-24"

RUN echo "バージョン(キャッシュ回避用に変更): 1.0.15.$(date +%s)-2"
RUN ln -sf /usr/share/zoneinfo/Asia/Tokyo /etc/localtime

RUN apt update;apt install wget curl net-tools tini tzdata -y;
RUN curl -fsSL https://tailscale.com/install.sh | sh
RUN rm -rf /var/lib/apt/lists/*
RUN wget https://github.com/gorcon/rcon-cli/releases/download/v0.10.3/rcon-0.10.3-amd64_linux.tar.gz -O rcon.tar.gz && tar -zxf rcon.tar.gz -C /tmp/ --wildcards rcon*/rcon && cp /tmp/rcon*/rcon /usr/local/bin/ && rm rcon.tar.gz && rm -r /tmp/rcon*

RUN wget https://raw.githubusercontent.com/sakkuntyo/docker-rust-server/refs/heads/main/launch.sh
RUN chmod +x launch.sh
RUN mkdir -p /root/rustserver
RUN mv launch.sh /root/rustserver

WORKDIR /root/rustserver
ENTRYPOINT tini -- ./launch.sh
