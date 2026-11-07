# Разбор KY FPV 2.0.0 — 6 ноября 2026

## Область и результат

Локальный статический анализ предоставленного пользователем APKS для совместимого Windows-клиента. Дрон отсутствует, динамический тест, захват трафика, установка APK, Hook и patch не выполнялись. Авторизация: запрос пользователя на разбор этого файла и создание приложения. Внешние устройства не сканировались.

SHA-256 APKS: `378FAFDE5FFA8366767C6C33EA20738B488A1E9F07C0CB85B544DC613514E218`.
Пакет `com.cooingdv.kyfpv`; базовый APK и ARM64 split извлечены в `analysis/bundle`.

## Компоненты и доказательства

Все относительные пути ниже — от корня проекта.

| ID | Источник | Вывод |
|---|---|---|
| E01 | analysis/apktool/AndroidManifest.xml | MainApplication, экспортированный launcher MainActivity; внутренний CommunicationService; INTERNET/Wi-Fi permissions |
| E02 | analysis/jadx/sources/com/cooingdv/kyfpv/tools/FlyController.java; одноимённый smali и FlyController$FlyControlTask.smali | Формат команды, 50 мс период, флаги, длительности импульсов |
| E03 | analysis/jadx/sources/com/cooingdv/kyfpv/fragment/DeviceTXFragment.java, startUdpTask / sendFlyControllerData / HeartBeatTask | Фактический UDP 192.168.1.1:7099, префикс 03, heartbeat 01 01 раз в секунду |
| E04 | analysis/jadx/sources/com/cooingdv/kyfpv/socket/Config.java | RTSP 192.168.1.1:7070/webcam. Константа SERVER_PORT=7070 не соответствует вызову startUdpTask; клиент использует 7099 |
| E05 | analysis/jadx/sources/com/cooingdv/kyfpv/utils/StreamClient.java | JNI к mjpeg_jni: streamStartServer, streamSendCommand, streamSwitchCamera, streamSetModify |
| E06 | analysis/native-disassembly.txt, libmjpeg_jni.so: 0x1b6c, 0x2044, 0x2840, rodata 0x3888 | BL подключается к 192.168.169.1:8800; Java DEFAULT_DEV_IP=192.168.8.15 нельзя использовать вместо native-адреса |
| E07 | analysis/native-disassembly.txt: 0x2c98 handle_mcu_msg_frag, 0x3488 build_send_ack | Фрагменты JPEG, таблицы качества, подтверждения приёма |
| E08 | analysis/jadx/sources/com/cooingdv/kyfpv/utils/FlyCommandUtils.java | Jieli: FLYING_CTRL / PUT / BYTE0…BYTE7 через DeviceClient, отдельный транспорт |

JADX имеет ошибки восстановления типов boolean/int и ветвлений в FlyControlTask; значимые флаги сверены со smali. Это не исходный Java-проект, и декомпиляция не обязана компилироваться.

## Полётный пакет

`66 R P T Y FLAGS XOR 99`, 8 байт. R/P/Y ограничены 1…255, нейтраль 128. T=1 преобразуется в 0. XOR — побитовое исключающее ИЛИ байтов 1…5.

| Бит FLAGS | Значение |
|---|---|
| 01 | взлёт, 1 с |
| 02 | посадка, 1 с |
| 04 | аварийная остановка, 1 с |
| 08 | завершение команды переворота |
| 10 | безголовый режим |
| 20 | fast return или unlock, неоднозначная функция в оригинале |
| 40 | свет |
| 80 | калибровка гироскопа, 2 с |

TX передаёт `03` + эти 8 байт. BL: `EF 04`, uint16 LE общая длина, uint32 LE sequence, uint16 LE длина payload, uint16 ноль, payload. Hello BL: `EF 00 04 00` приблизительно каждые 101 мс до установления потока. Реализация использует 110 мс; отличается от оригинального планировщика.

## BL-видео (экспериментальная реализация)

Вход: 93, тип, uint16 LE длина. Тип 1 — фрагмент. Offset 8: uint64 frame ID; 32: uint32 index; 36: uint32 count; 40: uint32 размер JPEG scan; 44/46: uint16 ширина/высота; 48: качество; 56: payload, до 1024 байт. Остаток последнего фрагмента — size modulo 1024. JPEG header 605 байт выбирается из Q5/10/25/50/75/100; высота/ширина записываются big endian на offsets 145/147; добавляется FF D9.

Таблицы извлечены из ELF .rodata через llvm-objcopy; сохранены как embedded resources в src/DroneControl/Headers. Android SO в Windows не загружается. Native ACK реализован частично: битовая карта текущего кадра и состояние завершения; не воспроизведены все очереди четырёх кадров, управление качеством и повторные запросы оригинала. Поэтому BL помечен опытным и требует сравнения с реальным трафиком.

## Безопасность и ограничения анализа

В восстановленном канале TX нет входа в аккаунт, подписи пакета и TLS; XOR не является криптографической защитой. BL-команды также передаются UDP без TLS. Это вывод только о рассмотренных каналах, не аудит всего APK. Наличие/отсутствие root detection, WebView и SSL pinning во всём приложении не устанавливалось. JNI подтверждён E05–E07. APK не изменялся, Hook не выполнялся.

Не установлено соответствие SSID Du-HD4K-1072B853 конкретному транспорту. Префикс SSID не использован как доказательство. Jieli и ряд функций оригинала не перенесены. Нельзя считать собранное приложение проверенным пультом управления этим дроном до стендового испытания.

## Воспроизведение и инструменты

- APKS распакован System.IO.Compression.ZipFile, ARM64 split отдельно.
- Apktool 3.0.3: Java 8, `java -jar C:/Windows/apktool.jar d analysis/bundle/base.apk -o analysis/apktool -p analysis/framework`.
- JADX установлен bootstrap навыка; запуск через JBR из Android Studio, `jadx --no-res -d analysis/jadx analysis/bundle/base.apk`.
- Generated tool-index.md в навыке отсутствовал. Использованы template, ToolDiscovery и проверенные Get-Command/поиск путей; фиктивные пути не подставлялись в итоговую цепочку.
- Native fallback: NDK 28.2.13676358 LLVM (llvm-nm, llvm-objdump, llvm-objcopy), установленный локально. Дизассемблер сохранён полностью.
- WPF SDK 10.0.301, без NuGet-зависимостей. FFmpeg для RTSP — пользовательский внешний executable.

## Проверка

`dotnet build src/DroneControl -c Release` и publish: успешно, 0 предупреждений и ошибок.
`dotnet run --project tests/DroneControl.Tests -c Release`: 17 проверок в итоговом запуске; включая loopback UDP. Loopback в песочнице дал таймаут, тот же тест вне песочницы успешно прошёл.

Checklist: triage выполнен; manifest и основной пакет прочитаны; Java и smali сопоставлены; native изучен; доказательства сохранены; приложение собрано; протокол проверен локально. Rebuild/подпись APK, установка, Frida и полётные испытания не требовались для статического анализа и не выполнялись. Проверка с настоящим дроном остаётся открытой.

Окно успешно запущено и отрисовано командой --render-check; снимок docs/window.png проверен визуально. Режим снимка не открывает UDP.

