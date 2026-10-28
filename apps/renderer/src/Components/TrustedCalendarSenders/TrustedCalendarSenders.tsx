import { useState } from "react";
import { Button, Select, SelectItem, TextInput } from "@carbon/react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { TrustedCalendarSenderScope } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/TrustedCalendarSenders/TrustedCalendarSenders.module.css";

/**
 * The senders whose calendar mail is believed without DKIM and DMARC to vouch for it. Added from an
 * invitation ("Always trust this sender") or here; removed here.
 */
export function TrustedCalendarSenders({ hub }: { hub: MailHubConnection }) {
	const queryClient = useQueryClient();
	const senders = useQuery({
		queryKey: ["trusted-calendar-senders"],
		queryFn: () => hub.getTrustedCalendarSenders(),
	});
	const refresh = () =>
		void queryClient.invalidateQueries({
			queryKey: ["trusted-calendar-senders"],
		});
	const [scope, setScope] = useState<TrustedCalendarSenderScope>(
		TrustedCalendarSenderScope.Domain,
	);
	const [value, setValue] = useState("");
	const add = useMutation({
		mutationFn: () => hub.addTrustedCalendarSender(scope, value),
		onSuccess: () => {
			setValue("");
			refresh();
		},
	});
	const remove = useMutation({
		mutationFn: (id: string) => hub.removeTrustedCalendarSender(id),
		onSuccess: refresh,
	});

	return (
		<section aria-labelledby="trusted-calendar-senders-title">
			<h4 id="trusted-calendar-senders-title">Trusted calendar senders</h4>
			<p className={styles.hint}>
				Invitations and replies from these senders are added to your calendar
				without the usual DKIM and DMARC check, which a domain without those DNS
				records can never pass. Anyone can forge a sender, so trust only people
				and domains you know.
			</p>
			<form
				className={styles.form}
				onSubmit={(event) => {
					event.preventDefault();
					if (value.trim()) add.mutate();
				}}
			>
				<Select
					id="trusted-sender-scope"
					labelText="Trust"
					size="sm"
					value={String(scope)}
					onChange={(event) => setScope(Number(event.target.value))}
				>
					<SelectItem
						value={String(TrustedCalendarSenderScope.Domain)}
						text="Domain"
					/>
					<SelectItem
						value={String(TrustedCalendarSenderScope.Sender)}
						text="Address"
					/>
				</Select>
				<TextInput
					id="trusted-sender-value"
					labelText="Sender to trust"
					size="sm"
					value={value}
					onChange={(event) => setValue(event.target.value)}
					placeholder={
						scope === TrustedCalendarSenderScope.Domain
							? "example.com"
							: "name@example.com"
					}
				/>
				<Button
					type="submit"
					size="sm"
					kind="tertiary"
					disabled={!value.trim() || add.isPending}
				>
					Trust
				</Button>
			</form>
			{senders.data?.length ? (
				<ul className={styles.list} aria-label="Trusted calendar senders">
					{senders.data.map((sender) => (
						<li key={sender.id} className={styles.item}>
							<span className={styles.kind}>
								{sender.scope === TrustedCalendarSenderScope.Domain
									? "Domain"
									: "Address"}
							</span>
							<span className={styles.value}>{sender.value}</span>
							<Button
								size="sm"
								kind="ghost"
								onClick={() => remove.mutate(sender.id)}
							>
								Remove
							</Button>
						</li>
					))}
				</ul>
			) : (
				<p className={styles.hint}>No trusted senders yet.</p>
			)}
		</section>
	);
}
