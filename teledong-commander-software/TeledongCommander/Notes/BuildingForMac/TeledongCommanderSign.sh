#!/bin/bash

APP_NAME="/Users/gitle2/Documents/Teledong Commander.app"
ENTITLEMENTS="/Users/gitle2/Documents/TeledongCommanderEntitlements.entitlements"
SIGNING_IDENTITY="Developer ID Application: Gitle Mikkelsen (Y62YL762Z5)"

find "$APP_NAME/Contents/MacOS/"|while read fname; do
	if [[ -f $fname ]]; then
		echo "[INFO] Signing $fname"
		codesign --force --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$fname"
	fi
done

echo "[INFO] Signing app file"

codesign --force --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$APP_NAME"