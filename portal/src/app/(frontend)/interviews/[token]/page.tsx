import type { Metadata } from 'next'

import { PublicInterviewPage } from '@/components/account/PublicInterviewPage'

export default async function InterviewPage({
  params,
}: {
  params: Promise<{ token: string }>
}) {
  const { token } = await params

  return <PublicInterviewPage token={token} />
}

export const metadata: Metadata = {
  title: 'Discovery Interview | ERP Generator',
  robots: {
    index: false,
    follow: false,
  },
}

